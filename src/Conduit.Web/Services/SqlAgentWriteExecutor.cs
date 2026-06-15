using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit.Connectors.SqlDiscovery;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Microsoft.Data.SqlClient;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "ApplySqlWrite" agent command: a single SQL Server
/// security DDL change (CREATE/DROP user/login, role membership) routed through
/// this Conduit job server, which holds the per-connection scan credential and
/// line-of-sight to the discovered SQL estate. IC enqueues the command over its
/// agent-command HTTP API; the poller hands the raw PayloadJson here.
///
/// This class owns ALL trust-boundary enforcement. IC is a SEPARATE trust domain:
/// nothing in the payload is taken on faith — not the operation, not the role
/// name, and ABOVE ALL not any identifier that ends up in DDL.
///
/// T-SQL injection is the headline threat. No principal / database / login / role
/// name is EVER concatenated into a DDL string. Every identifier is first
/// validated against a strict whitelist (reject anything outside it), then passed
/// as an nvarchar PARAMETER to sp_executesql where QUOTENAME() escapes it
/// server-side. Database scope is set via the connection's InitialCatalog — never
/// a concatenated USE statement. Operations and role names are closed allow-lists
/// checked before any connection is opened.
///
/// Security constraints enforced here:
///   1  Strict typed parse; >64 KB → hard failure; schemaVersion must be 1.
///   2  operation allow-list (closed set of 8); anything else → fail, no connect.
///   3  identifier validation: principal/login/role/db pass a strict whitelist.
///   4  QUOTENAME + sp_executesql PARAMETERS — identifiers never concatenated.
///   5  database scope via InitialCatalog, never a concatenated USE.
///   6  role allow-lists: fixed db-role set and fixed server-role set.
///   7  privileged guards: cannot drop 'sa' / built-ins / the scan credential's
///      own login; granting sysadmin/securityadmin/db_owner/db_securityadmin is
///      permitted (IC enforces step-up) but called out prominently in the result.
///   8  Encrypt=Mandatory for SqlAuth (same posture as the discovery scanner) so
///      the scan credential is never sent in cleartext to a discovered host.
///   9  never throws to the caller; never logs the raw payload; SqlException
///      messages are first-line-only and never carry the connection string.
/// </summary>
public sealed class SqlAgentWriteExecutor
{
    private const int MaxPayloadBytes = 64 * 1024;

    private const string OpCreateDbUserFromLogin = "CreateDbUserFromLogin";
    private const string OpDropDbUser = "DropDbUser";
    private const string OpCreateWindowsLogin = "CreateWindowsLogin";
    private const string OpDropLogin = "DropLogin";
    private const string OpAddDatabaseRoleMember = "AddDatabaseRoleMember";
    private const string OpRemoveDatabaseRoleMember = "RemoveDatabaseRoleMember";
    private const string OpAddServerRoleMember = "AddServerRoleMember";
    private const string OpRemoveServerRoleMember = "RemoveServerRoleMember";

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        OpCreateDbUserFromLogin, OpDropDbUser, OpCreateWindowsLogin, OpDropLogin,
        OpAddDatabaseRoleMember, OpRemoveDatabaseRoleMember, OpAddServerRoleMember, OpRemoveServerRoleMember
    };

    /// <summary>Closed set of fixed database roles membership ops may target.</summary>
    private static readonly HashSet<string> AllowedDatabaseRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "db_datareader", "db_datawriter", "db_owner", "db_securityadmin",
        "db_accessadmin", "db_ddladmin", "db_denydatareader", "db_denydatawriter"
    };

    /// <summary>Closed set of fixed server roles membership ops may target.</summary>
    private static readonly HashSet<string> AllowedServerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysadmin", "securityadmin", "serveradmin", "setupadmin",
        "processadmin", "diskadmin", "dbcreator", "bulkadmin"
    };

    /// <summary>Highly-privileged roles — granting them is allowed but flagged loudly.</summary>
    private static readonly HashSet<string> PrivilegedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysadmin", "securityadmin", "db_owner", "db_securityadmin"
    };

    /// <summary>Built-in logins that must never be dropped.</summary>
    private static readonly HashSet<string> ProtectedLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "sa", "public",
        "##MS_PolicyEventProcessingLogin##", "##MS_PolicyTsqlExecutionLogin##",
        "NT AUTHORITY\\SYSTEM", "NT SERVICE\\MSSQLSERVER", "NT SERVICE\\SQLSERVERAGENT",
        "BUILTIN\\Administrators"
    };

    private readonly CredentialProtector _protector;
    private readonly SinkConnectionCredentialMapRepository _credentialMap;
    private readonly TenantRepository _tenants;
    private readonly ILogger<SqlAgentWriteExecutor> _logger;

    public SqlAgentWriteExecutor(
        CredentialProtector protector,
        SinkConnectionCredentialMapRepository credentialMap,
        TenantRepository tenants,
        ILogger<SqlAgentWriteExecutor> logger)
    {
        _protector = protector;
        _credentialMap = credentialMap;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>
    /// Validate + execute an ApplySqlWrite payload. Returns (success, message) for
    /// the poller's complete callback. NEVER throws to the caller and NEVER logs
    /// the raw payload body — only command-shaped facts (operation + result).
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) Strict, size-bounded parse ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return (false, "ApplySqlWrite: empty payload.");
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return (false, $"ApplySqlWrite: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        ApplySqlWritePayload? p;
        try
        {
            p = JsonSerializer.Deserialize<ApplySqlWritePayload>(payloadJson, StrictJson);
        }
        catch (JsonException)
        {
            return (false, "ApplySqlWrite: malformed payload JSON.");
        }
        if (p is null)
            return (false, "ApplySqlWrite: payload deserialized to null.");

        if (p.SchemaVersion != 1)
            return (false, $"ApplySqlWrite: unsupported schemaVersion {p.SchemaVersion}.");

        // ── (2) operation allow-list (closed set) — before any connection ────
        var operation = p.Operation?.Trim();
        if (string.IsNullOrEmpty(operation) || !AllowedOperations.Contains(operation))
            return (false, $"ApplySqlWrite: operation '{p.Operation}' is not allowed.");

        var serverName = p.ServerName?.Trim();
        if (string.IsNullOrEmpty(serverName))
            return (false, "ApplySqlWrite: serverName is required.");
        // serverName is the DataSource, never injected into DDL; still reject the
        // semicolon/quote shapes that could break out of a connection string.
        if (serverName.IndexOfAny(new[] { ';', '\'', '"', '\r', '\n', '\0' }) >= 0)
            return (false, "ApplySqlWrite: serverName contains illegal characters.");

        // ── Credential resolution (Conduit owns it) ─────────────────────────────
        // The caller-supplied connectionId is IGNORED for credential selection.
        // We resolve the Conduit tenant whose 'sqldiscovery' credential backs this
        // write from the server-trusted sourceConnectionName via the orchestrator-
        // owned mapping. No mapping / disabled tenant / missing credential → fail
        // closed; never fall back to connectionId or a default tenant.
        var sourceConnectionName = p.SourceConnectionName?.Trim();
        if (string.IsNullOrEmpty(sourceConnectionName))
            return (false, "ApplySqlWrite: sourceConnectionName is missing.");

        var resolvedTenantId = await _credentialMap.GetTenantIdByNameAsync(sourceConnectionName);
        if (resolvedTenantId is null || resolvedTenantId.Value == Guid.Empty)
            return (false, $"ApplySqlWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        var tenant = await _tenants.GetByIdAsync(resolvedTenantId.Value);
        if (tenant is null || !tenant.IsActive)
            return (false, $"ApplySqlWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        // ── Load the scan credential for the resolved tenant (same blob the source uses) ──
        SqlDiscoveryConfig? config;
        try
        {
            config = await SqlDiscoveryConfigReader.ReadAsync(_protector, resolvedTenantId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplySqlWrite {CommandId}: failed reading sqldiscovery credential.", commandId);
            return (false, "ApplySqlWrite: could not read the SQL discovery credential for this connection.");
        }
        if (config is null)
            return (false, $"ApplySqlWrite: No Conduit credential mapping for source connection '{sourceConnectionName}'. Run a sync from this connection to register it.");

        try
        {
            return operation switch
            {
                OpCreateDbUserFromLogin => await DoCreateDbUserFromLoginAsync(serverName, config, p, ct),
                OpDropDbUser => await DoDropDbUserAsync(serverName, config, p, ct),
                OpCreateWindowsLogin => await DoCreateWindowsLoginAsync(serverName, config, p, ct),
                OpDropLogin => await DoDropLoginAsync(serverName, config, p, ct),
                OpAddDatabaseRoleMember => await DoDatabaseRoleMemberAsync(serverName, config, p, add: true, ct),
                OpRemoveDatabaseRoleMember => await DoDatabaseRoleMemberAsync(serverName, config, p, add: false, ct),
                OpAddServerRoleMember => await DoServerRoleMemberAsync(serverName, config, p, add: true, ct),
                OpRemoveServerRoleMember => await DoServerRoleMemberAsync(serverName, config, p, add: false, ct),
                _ => (false, $"ApplySqlWrite: operation '{operation}' is not allowed.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "ApplySqlWrite {CommandId} ({Operation}) SQL error.", commandId, operation);
            return (false, $"ApplySqlWrite: {operation} failed: {FirstLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            // Never leak payload content; log command-shaped facts only.
            _logger.LogError(ex, "ApplySqlWrite {CommandId} ({Operation}) threw.", commandId, operation);
            return (false, $"ApplySqlWrite: {operation} failed: {FirstLine(ex.Message)}");
        }
    }

    // ── CreateDbUserFromLogin ────────────────────────────────────────────────
    private async Task<(bool, string)> DoCreateDbUserFromLoginAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, CancellationToken ct)
    {
        if (!TryValidateDatabaseName(p.DatabaseName, out var db, out var dbErr)) return (false, dbErr);
        if (!TryValidatePrincipalName(p.PrincipalName, out var principal, out var pErr)) return (false, pErr);
        if (!TryValidateLoginName(p.LoginName, out var login, out var lErr)) return (false, lErr);

        await using var conn = await OpenAsync(serverName, config, db, ct);

        // Guard: the login must already exist (CREATE USER FOR LOGIN otherwise errors,
        // but a clean pre-check returns a clearer message than the SQL error).
        if (!await LoginExistsAsync(conn, login, ct))
            return (false, $"CreateDbUserFromLogin: server login '{login}' does not exist.");

        const string sql =
            "DECLARE @s nvarchar(max) = N'CREATE USER ' + QUOTENAME(@principal) + N' FOR LOGIN ' + QUOTENAME(@login); EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@principal", principal);
        cmd.Parameters.AddWithValue("@login", login);
        await cmd.ExecuteNonQueryAsync(ct);
        return (true, $"Created database user [{principal}] for login [{login}] in [{db}] on {serverName}.");
    }

    // ── DropDbUser ────────────────────────────────────────────────────────────
    private async Task<(bool, string)> DoDropDbUserAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, CancellationToken ct)
    {
        if (!TryValidateDatabaseName(p.DatabaseName, out var db, out var dbErr)) return (false, dbErr);
        if (!TryValidatePrincipalName(p.PrincipalName, out var principal, out var pErr)) return (false, pErr);

        if (string.Equals(principal, "dbo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(principal, "guest", StringComparison.OrdinalIgnoreCase))
            return (false, $"DropDbUser: refusing to drop built-in database user '{principal}'.");

        await using var conn = await OpenAsync(serverName, config, db, ct);

        const string sql =
            "DECLARE @s nvarchar(max) = N'DROP USER ' + QUOTENAME(@principal); EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@principal", principal);
        await cmd.ExecuteNonQueryAsync(ct);
        return (true, $"Dropped database user [{principal}] from [{db}] on {serverName}.");
    }

    // ── CreateWindowsLogin ────────────────────────────────────────────────────
    private async Task<(bool, string)> DoCreateWindowsLoginAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, CancellationToken ct)
    {
        // For this op the Windows principal arrives in loginName (DOMAIN\name).
        if (!TryValidateWindowsLoginName(p.LoginName, out var login, out var lErr)) return (false, lErr);

        await using var conn = await OpenAsync(serverName, config, databaseName: null, ct);

        const string sql =
            "DECLARE @s nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login) + N' FROM WINDOWS'; EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@login", login);
        await cmd.ExecuteNonQueryAsync(ct);
        return (true, $"Created Windows login [{login}] on {serverName}.");
    }

    // ── DropLogin ─────────────────────────────────────────────────────────────
    private async Task<(bool, string)> DoDropLoginAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, CancellationToken ct)
    {
        if (!TryValidatePrincipalName(p.PrincipalName, out var login, out var pErr)) return (false, pErr);

        // (7) refuse built-ins and the scan credential's own login.
        if (ProtectedLogins.Contains(login))
            return (false, $"DropLogin: refusing to drop protected built-in login '{login}'.");
        if (string.Equals(config.AuthType, "SqlAuth", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(config.SqlUsername)
            && string.Equals(login, config.SqlUsername, StringComparison.OrdinalIgnoreCase))
            return (false, "DropLogin: refusing to drop the connection's own scan credential login.");

        await using var conn = await OpenAsync(serverName, config, databaseName: null, ct);

        // Refuse to drop a sysadmin member (defense in depth; IC gates too).
        if (await IsSysadminMemberAsync(conn, login, ct))
            return (false, $"DropLogin: refusing to drop sysadmin member '{login}'.");

        const string sql =
            "DECLARE @s nvarchar(max) = N'DROP LOGIN ' + QUOTENAME(@login); EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@login", login);
        await cmd.ExecuteNonQueryAsync(ct);
        return (true, $"Dropped login [{login}] on {serverName}.");
    }

    // ── Add / Remove database role member ─────────────────────────────────────
    private async Task<(bool, string)> DoDatabaseRoleMemberAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, bool add, CancellationToken ct)
    {
        var verb = add ? "AddDatabaseRoleMember" : "RemoveDatabaseRoleMember";
        if (!TryValidateDatabaseName(p.DatabaseName, out var db, out var dbErr)) return (false, dbErr);
        if (!TryValidatePrincipalName(p.PrincipalName, out var principal, out var pErr)) return (false, pErr);
        if (!TryValidateRoleName(p.RoleName, out var role, out var rErr)) return (false, rErr);

        // (6) closed db-role allow-list.
        if (!AllowedDatabaseRoles.Contains(role))
            return (false, $"{verb}: database role '{role}' is not in the allow-list.");

        await using var conn = await OpenAsync(serverName, config, db, ct);

        var action = add ? "ADD" : "DROP";
        var sql =
            "DECLARE @s nvarchar(max) = N'ALTER ROLE ' + QUOTENAME(@role) + N' " + action + " MEMBER ' + QUOTENAME(@principal); EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@principal", principal);
        await cmd.ExecuteNonQueryAsync(ct);

        var grantNote = add && PrivilegedRoles.Contains(role) ? " [PRIVILEGED GRANT]" : string.Empty;
        var ok = add
            ? $"Added [{principal}] to database role [{role}] in [{db}] on {serverName}.{grantNote}"
            : $"Removed [{principal}] from database role [{role}] in [{db}] on {serverName}.";
        return (true, ok);
    }

    // ── Add / Remove server role member ───────────────────────────────────────
    private async Task<(bool, string)> DoServerRoleMemberAsync(
        string serverName, SqlDiscoveryConfig config, ApplySqlWritePayload p, bool add, CancellationToken ct)
    {
        var verb = add ? "AddServerRoleMember" : "RemoveServerRoleMember";
        if (!TryValidatePrincipalName(p.PrincipalName, out var principal, out var pErr)) return (false, pErr);
        if (!TryValidateRoleName(p.RoleName, out var role, out var rErr)) return (false, rErr);

        // (6) closed server-role allow-list.
        if (!AllowedServerRoles.Contains(role))
            return (false, $"{verb}: server role '{role}' is not in the allow-list.");

        await using var conn = await OpenAsync(serverName, config, databaseName: null, ct);

        var action = add ? "ADD" : "DROP";
        var sql =
            "DECLARE @s nvarchar(max) = N'ALTER SERVER ROLE ' + QUOTENAME(@role) + N' " + action + " MEMBER ' + QUOTENAME(@principal); EXEC sp_executesql @s;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@principal", principal);
        await cmd.ExecuteNonQueryAsync(ct);

        var grantNote = add && PrivilegedRoles.Contains(role) ? " [PRIVILEGED GRANT]" : string.Empty;
        var ok = add
            ? $"Added [{principal}] to server role [{role}] on {serverName}.{grantNote}"
            : $"Removed [{principal}] from server role [{role}] on {serverName}.";
        return (true, ok);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a connection to <paramref name="serverName"/> using the discovery scan
    /// credential, the SAME auth/Encrypt pattern as SqlServerScanner. The database
    /// scope (when required) is set via InitialCatalog — never a concatenated USE.
    /// </summary>
    private static async Task<SqlConnection> OpenAsync(
        string serverName, SqlDiscoveryConfig config, string? databaseName, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = string.IsNullOrEmpty(databaseName) ? "master" : databaseName,
            ConnectTimeout = config.ConnectTimeoutSeconds,
            TrustServerCertificate = config.TrustServerCertificate,
            Pooling = false,
            ApplicationName = "Conduit SQL Write"
        };
        if (string.Equals(config.AuthType, "WindowsAuth", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
            builder.Encrypt = SqlConnectionEncryptOption.Optional;
        }
        else
        {
            // SqlAuth sends the scan credential to the target host — TLS mandatory
            // so it is never harvested in cleartext (matches the discovery scanner).
            // CAVEAT: config.TrustServerCertificate=true (lab/self-signed estates)
            // encrypts but does NOT validate the server cert — a MITM could still
            // harvest the credential. The write path honors the same config as the
            // scanner for lab parity; tightening this to forced cert validation on
            // writes is tracked as a follow-up (Worf HIGH, 2026-06-14).
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
            builder.UserID = config.SqlUsername ?? string.Empty;
            builder.Password = config.SqlPassword ?? string.Empty;
        }

        var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task<bool> LoginExistsAsync(SqlConnection conn, string login, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM sys.server_principals WHERE name = @login;", conn);
        cmd.Parameters.AddWithValue("@login", login);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IsSysadminMemberAsync(SqlConnection conn, string login, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT IS_SRVROLEMEMBER(N'sysadmin', @login);", conn);
        cmd.Parameters.AddWithValue("@login", login);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i && i == 1;
    }

    // ── (3) Identifier validation — strict whitelists, reject everything else ──

    /// <summary>
    /// Principal / database-user / login names: letters, digits, underscore, and a
    /// conservative set of sAMAccountName-shaped characters (space, dot, hyphen,
    /// dollar). A single backslash is permitted (DOMAIN\name). Rejects every
    /// metacharacter that could break out of (or comment out) DDL even though
    /// QUOTENAME also escapes it — defense in depth.
    /// </summary>
    private static bool TryValidatePrincipalName(string? raw, out string value, out string error)
        => TryValidateIdentifier(raw, "principalName", allowBackslash: true, out value, out error);

    private static bool TryValidateLoginName(string? raw, out string value, out string error)
        => TryValidateIdentifier(raw, "loginName", allowBackslash: true, out value, out error);

    private static bool TryValidateRoleName(string? raw, out string value, out string error)
        => TryValidateIdentifier(raw, "roleName", allowBackslash: false, out value, out error);

    private static bool TryValidateDatabaseName(string? raw, out string value, out string error)
        => TryValidateIdentifier(raw, "databaseName", allowBackslash: false, out value, out error);

    private static bool TryValidateIdentifier(string? raw, string field, bool allowBackslash, out string value, out string error)
    {
        value = string.Empty;
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            error = $"ApplySqlWrite: {field} is required.";
            return false;
        }
        if (s.Length > 128)
        {
            error = $"ApplySqlWrite: {field} exceeds 128 characters.";
            return false;
        }
        foreach (var ch in s)
        {
            var ok = char.IsLetterOrDigit(ch)
                     || ch == '_' || ch == ' ' || ch == '.' || ch == '-' || ch == '$'
                     || (allowBackslash && ch == '\\');
            if (!ok)
            {
                error = $"ApplySqlWrite: {field} contains an illegal character.";
                return false;
            }
        }
        // Explicitly reject comment / statement-terminator sequences as a belt-and-
        // suspenders check (the char loop already excludes ';', '\'', ']' etc.).
        if (s.Contains("--", StringComparison.Ordinal)
            || s.Contains("/*", StringComparison.Ordinal)
            || s.Contains("*/", StringComparison.Ordinal))
        {
            error = $"ApplySqlWrite: {field} contains an illegal sequence.";
            return false;
        }
        value = s;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Windows login for CREATE LOGIN ... FROM WINDOWS: must be the DOMAIN\name form
    /// (exactly one backslash, non-empty on both sides). The name part follows the
    /// same conservative whitelist as a principal.
    /// </summary>
    private static bool TryValidateWindowsLoginName(string? raw, out string value, out string error)
    {
        value = string.Empty;
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            error = "ApplySqlWrite: loginName (DOMAIN\\name) is required.";
            return false;
        }
        var slash = s.IndexOf('\\');
        if (slash <= 0 || slash != s.LastIndexOf('\\') || slash == s.Length - 1)
        {
            error = "ApplySqlWrite: loginName must be a single DOMAIN\\name.";
            return false;
        }
        // Validate the whole thing through the standard identifier check (backslash
        // allowed); the structural check above guarantees the single-backslash form.
        return TryValidateIdentifier(s, "loginName", allowBackslash: true, out value, out error);
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var idx = message.IndexOf('\n');
        var line = idx >= 0 ? message[..idx] : message;
        line = line.Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    /// <summary>
    /// Strict typed model for the IC ApplySqlWrite payload (schemaVersion 1). NO
    /// password / secret / token field exists here by design — SQL-auth login
    /// creation WITH PASSWORD is out of scope (deferred).
    /// </summary>
    private sealed class ApplySqlWritePayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("objectGuid")] public string? ObjectGuid { get; set; }       // advisory provenance only
        [JsonPropertyName("connectionId")] public string? ConnectionId { get; set; }   // transition only — IGNORED for credential selection
        [JsonPropertyName("sourceConnectionName")] public string? SourceConnectionName { get; set; }  // server-resolved IC DirectoryConnections.Name; the credential selector
        [JsonPropertyName("serverName")] public string? ServerName { get; set; }
        [JsonPropertyName("operation")] public string? Operation { get; set; }
        [JsonPropertyName("databaseName")] public string? DatabaseName { get; set; }
        [JsonPropertyName("principalName")] public string? PrincipalName { get; set; }
        [JsonPropertyName("loginName")] public string? LoginName { get; set; }
        [JsonPropertyName("roleName")] public string? RoleName { get; set; }
    }
}
