using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// AD sink via System.DirectoryServices.Protocols. Pumps ConnectorObjects into
/// AD as create-or-update. Idempotent: looks up by sAMAccountName / userPrincipalName
/// / distinguishedName / objectGUID and either ADDs or MODIFYs.
///
/// Clean-room rewrite of IC's 1500-line DirectoryWriteService — IC uses ADSI
/// (DirectoryEntry); Conduit uses S.DS.P to match the Phase 1A source side and
/// to stay portable (no System.DirectoryServices p/invoke wedge on Linux).
///
/// === Phase 1C parity decisions (2026-05-23) ===
///   Default target OU:
///       When the project scope's BaseDN is blank, query RootDSE for
///       defaultNamingContext (with rootDomainNamingContext fallback). This
///       matches IC's DirectoryQueryService.GetDefaultNamingContextAnonymous +
///       DirectorySchemaService discovery pattern. NO silent "CN=Users,..." —
///       if RootDSE can't be reached, the write fails loud.
///
///   Password writes (unicodePwd):
///       HARD requirement: the connection must be on LDAPS (port 636) or have
///       successfully negotiated StartTLS. Soft-warn-and-continue is gone. A
///       password attribute on a non-secure connection throws — operators get
///       a clear error rather than a half-provisioned (disabled) account.
///
///   Group memberships:
///       Full-replace (not delta). The orchestrator delivers the complete
///       member list per group; we Replace the 'member' attribute wholesale.
///       Matches the Emulator sink and IC's current behavior post-V115.
/// </summary>
public sealed class ActiveDirectorySink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILogger<ActiveDirectorySink> _logger;
    private readonly string? _defaultBaseDn;

    private const int ADS_UF_ACCOUNTDISABLE = 0x0002;
    private const int ADS_UF_NORMAL_ACCOUNT = 0x0200;

    public ActiveDirectorySink(
        Guid tenantId,
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILogger<ActiveDirectorySink> logger,
        string? defaultBaseDn = null)
    {
        _tenantId = tenantId;
        _tenantRepo = tenantRepo;
        _protector = protector;
        _logger = logger;
        _defaultBaseDn = defaultBaseDn;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            // Phase 3 tombstone marker: source emitted a delete signal. We do NOT
            // actually delete in AD from the sink (irreversible + dangerous). The
            // honest behavior is "skip + log" — operators run delete intent through
            // the lifecycle/governance product, not the sync engine.
            if (obj.Attributes.TryGetValue("_deleted", out var dv) && dv is bool db && db)
            {
                _logger.LogInformation("AD sink saw _deleted marker for {SourceId} ({DN}) — not deleting (governance-side decision).",
                    obj.SourceId,
                    obj.Attributes.TryGetValue("distinguishedName", out var ddn) ? ddn : "<no DN>");
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
            }

            var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
                ?? throw new InvalidOperationException($"Tenant {_tenantId} not found.");
            var creds = await ReadCredsAsync()
                ?? throw new InvalidOperationException(
                    $"No 'ldap' credential stored for tenant {_tenantId}. Save credentials before running.");

            using var connection = Bind(tenant.Domain, creds, out var isSecure);

            return string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? UpsertGroup(connection, obj)
                : UpsertUser(connection, obj, isSecure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD sink upsert failed for SourceId={SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
                ?? throw new InvalidOperationException($"Tenant {_tenantId} not found.");
            var creds = await ReadCredsAsync()
                ?? throw new InvalidOperationException("No 'ldap' credential stored.");

            using var connection = Bind(tenant.Domain, creds, out _);

            // sourceId may be a DN or an objectGUID — try both.
            var dn = LooksLikeDn(sourceId)
                ? sourceId
                : FindDnByGuid(connection, sourceId);

            if (string.IsNullOrEmpty(dn))
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped); // idempotent: nothing to delete.

            connection.SendRequest(new DeleteRequest(dn));
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD sink delete failed for SourceId={SourceId}", sourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId);
            if (tenant is null) return new ConnectorTestResult { IsSuccessful = false, Message = "Tenant not found." };
            var creds = await ReadCredsAsync();
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'ldap' credential stored." };
            using var conn = Bind(tenant.Domain, creds, out var isSecure);
            var transport = isSecure ? "LDAPS" : "LDAP";
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Bound for write over {transport}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── User upsert ───────────────────────────────────────────────────────

    private SinkWriteResult UpsertUser(LdapConnection conn, ConnectorObject obj, bool isSecure)
    {
        string? sam = GetStr(obj, "sAMAccountName") ?? GetStr(obj, "userName") ?? GetStr(obj, "UserName");
        string? upn = GetStr(obj, "userPrincipalName") ?? GetStr(obj, "UserPrincipalName");
        string? dnFromSource = LooksLikeDn(obj.SourceId) ? obj.SourceId : null;
        string? guid = !LooksLikeDn(obj.SourceId) ? obj.SourceId : null;

        string? existingDn = null;
        if (!string.IsNullOrEmpty(dnFromSource)) existingDn = DnExists(conn, dnFromSource) ? dnFromSource : null;
        if (existingDn is null && !string.IsNullOrEmpty(guid)) existingDn = FindDnByGuid(conn, guid!);
        if (existingDn is null && !string.IsNullOrEmpty(sam)) existingDn = FindDnByFilter(conn, $"(sAMAccountName={EscapeFilter(sam!)})");
        if (existingDn is null && !string.IsNullOrEmpty(upn)) existingDn = FindDnByFilter(conn, $"(userPrincipalName={EscapeFilter(upn!)})");

        if (existingDn is not null)
        {
            ModifyUser(conn, existingDn, obj, isSecure);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }

        if (string.IsNullOrWhiteSpace(sam))
            return SinkWriteResult.Fail("Cannot create AD user without a sAMAccountName / userName mapping.");

        var baseDn = ResolveBaseDn(conn, obj);
        if (string.IsNullOrWhiteSpace(baseDn))
            return SinkWriteResult.Fail(
                "Cannot create AD user: no target Base DN. Set scope.BaseDN on the sync project, or " +
                "ensure RootDSE.defaultNamingContext is reachable on this connection.");

        var cn = GetStr(obj, "cn") ?? GetStr(obj, "displayName") ?? sam!;
        var newDn = $"CN={EscapeDnComponent(cn)},{baseDn}";

        var addRequest = new AddRequest(newDn,
            new DirectoryAttribute("objectClass", new[] { "top", "person", "organizationalPerson", "user" }));
        addRequest.Attributes.Add(new DirectoryAttribute("sAMAccountName", sam));
        if (!string.IsNullOrEmpty(upn)) addRequest.Attributes.Add(new DirectoryAttribute("userPrincipalName", upn));
        AppendIfPresent(addRequest, obj, "displayName");
        AppendIfPresent(addRequest, obj, "givenName");
        AppendIfPresent(addRequest, obj, "sn", "familyName");
        AppendIfPresent(addRequest, obj, "mail", "email");
        AppendIfPresent(addRequest, obj, "title");
        AppendIfPresent(addRequest, obj, "department");
        AppendIfPresent(addRequest, obj, "company");
        AppendIfPresent(addRequest, obj, "employeeID", "employeeNumber");
        AppendIfPresent(addRequest, obj, "telephoneNumber", "phoneNumber");

        // New accounts are created disabled by default until a password lands;
        // we'll flip the enable bit after the password write below.
        var requestedEnabled = !TryGetBool(obj, "active", out var a) || a;
        var initialUac = ADS_UF_NORMAL_ACCOUNT | ADS_UF_ACCOUNTDISABLE;
        addRequest.Attributes.Add(new DirectoryAttribute("userAccountControl", initialUac.ToString()));

        conn.SendRequest(addRequest);

        // Password write — HARD-required LDAPS. If a password was supplied on a
        // plaintext-bound connection we throw rather than create a permanently
        // disabled, no-password account that's worse than not creating at all.
        var password = GetStr(obj, "password") ?? GetStr(obj, "userPassword");
        if (!string.IsNullOrEmpty(password))
        {
            RequireSecureForPasswordWrite(isSecure);
            SetPassword(conn, newDn, password!);
        }

        if (requestedEnabled && !string.IsNullOrEmpty(password))
        {
            SetUacFlag(conn, newDn, ADS_UF_ACCOUNTDISABLE, set: false);
        }

        return SinkWriteResult.Ok(SinkWriteOutcome.Created);
    }

    private void ModifyUser(LdapConnection conn, string dn, ConnectorObject obj, bool isSecure)
    {
        var mods = new List<DirectoryAttributeModification>();
        AddReplace(mods, obj, "displayName");
        AddReplace(mods, obj, "givenName");
        AddReplace(mods, obj, "sn", "familyName");
        AddReplace(mods, obj, "mail", "email");
        AddReplace(mods, obj, "title");
        AddReplace(mods, obj, "department");
        AddReplace(mods, obj, "company");
        AddReplace(mods, obj, "employeeID", "employeeNumber");
        AddReplace(mods, obj, "telephoneNumber", "phoneNumber");
        AddReplace(mods, obj, "userPrincipalName", "UserPrincipalName");

        if (mods.Count > 0)
        {
            var modify = new ModifyRequest(dn, mods.ToArray());
            conn.SendRequest(modify);
        }

        if (TryGetBool(obj, "active", out var active))
        {
            SetUacFlag(conn, dn, ADS_UF_ACCOUNTDISABLE, set: !active);
        }

        var password = GetStr(obj, "password") ?? GetStr(obj, "userPassword");
        if (!string.IsNullOrEmpty(password))
        {
            RequireSecureForPasswordWrite(isSecure);
            SetPassword(conn, dn, password!);
        }
    }

    // ─── Group upsert ──────────────────────────────────────────────────────

    private SinkWriteResult UpsertGroup(LdapConnection conn, ConnectorObject obj)
    {
        string? sam = GetStr(obj, "sAMAccountName") ?? GetStr(obj, "displayName") ?? GetStr(obj, "cn");
        string? dnFromSource = LooksLikeDn(obj.SourceId) ? obj.SourceId : null;
        string? guid = !LooksLikeDn(obj.SourceId) ? obj.SourceId : null;

        string? existingDn = null;
        if (!string.IsNullOrEmpty(dnFromSource)) existingDn = DnExists(conn, dnFromSource) ? dnFromSource : null;
        if (existingDn is null && !string.IsNullOrEmpty(guid)) existingDn = FindDnByGuid(conn, guid!);
        if (existingDn is null && !string.IsNullOrEmpty(sam)) existingDn = FindDnByFilter(conn, $"(&(objectCategory=group)(sAMAccountName={EscapeFilter(sam!)}))");

        if (existingDn is not null)
        {
            var mods = new List<DirectoryAttributeModification>();
            AddReplace(mods, obj, "description");
            AddReplace(mods, obj, "displayName");
            // Full-replace membership semantics — match Emulator + IC. The
            // orchestrator delivers the complete member DN list per group on
            // every run; we Replace 'member' wholesale (no diff/delta).
            ReplaceMembers(mods, obj);
            if (mods.Count > 0)
                conn.SendRequest(new ModifyRequest(existingDn, mods.ToArray()));
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }

        if (string.IsNullOrWhiteSpace(sam))
            return SinkWriteResult.Fail("Cannot create AD group without a sAMAccountName / cn mapping.");

        var baseDn = ResolveBaseDn(conn, obj);
        if (string.IsNullOrWhiteSpace(baseDn))
            return SinkWriteResult.Fail(
                "Cannot create AD group: no target Base DN. Set scope.BaseDN on the sync project, or " +
                "ensure RootDSE.defaultNamingContext is reachable on this connection.");

        var cn = GetStr(obj, "cn") ?? sam!;
        var newDn = $"CN={EscapeDnComponent(cn)},{baseDn}";
        var addRequest = new AddRequest(newDn,
            new DirectoryAttribute("objectClass", new[] { "top", "group" }));
        addRequest.Attributes.Add(new DirectoryAttribute("sAMAccountName", sam));
        AppendIfPresent(addRequest, obj, "description");
        AppendIfPresent(addRequest, obj, "displayName");
        // Members on create — same full-set semantics, expressed as an initial
        // multi-valued add of the 'member' attribute.
        AppendMembersOnCreate(addRequest, obj);
        conn.SendRequest(addRequest);
        return SinkWriteResult.Ok(SinkWriteOutcome.Created);
    }

    // ─── Phase 7 person-aware step methods ──────────────────────────────────
    //
    // AD doesn't have an IC-shaped "Person" concept (User and Person are the
    // same thing here), so MatchPerson / CreatePerson stay as NotSupported —
    // the contract default already throws. We DO implement the two relational
    // ones because they're standard LDAP attribute writes:
    //   * `manager`   on a user, value = DN of the manager
    //   * `managedBy` on a group, value = DN of the owner

    public async Task<SinkWriteResult> AssignManagerAsync(string objectExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
                ?? throw new InvalidOperationException("Tenant not found.");
            var creds = await ReadCredsAsync()
                ?? throw new InvalidOperationException("No 'ldap' credential.");
            using var conn = Bind(tenant.Domain, creds, out _);

            var targetDn = ResolveToDn(conn, objectExternalId);
            if (targetDn is null) return SinkWriteResult.Fail($"User '{objectExternalId}' not found in AD.");
            var managerDn = ResolveToDn(conn, managerExternalId);
            if (managerDn is null) return SinkWriteResult.Fail($"Manager '{managerExternalId}' not found in AD.");

            var mod = new DirectoryAttributeModification
            {
                Name = "manager",
                Operation = DirectoryAttributeOperation.Replace
            };
            mod.Add(managerDn);
            conn.SendRequest(new ModifyRequest(targetDn, mod));
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD AssignManager failed (tenant={TenantId}, target={Target})", _tenantId, objectExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> AssignGroupOwnerAsync(string groupExternalId, string ownerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
                ?? throw new InvalidOperationException("Tenant not found.");
            var creds = await ReadCredsAsync()
                ?? throw new InvalidOperationException("No 'ldap' credential.");
            using var conn = Bind(tenant.Domain, creds, out _);

            var groupDn = ResolveToDn(conn, groupExternalId);
            if (groupDn is null) return SinkWriteResult.Fail($"Group '{groupExternalId}' not found in AD.");
            var ownerDn = ResolveToDn(conn, ownerExternalId);
            if (ownerDn is null) return SinkWriteResult.Fail($"Owner '{ownerExternalId}' not found in AD.");

            var mod = new DirectoryAttributeModification
            {
                Name = "managedBy",
                Operation = DirectoryAttributeOperation.Replace
            };
            mod.Add(ownerDn);
            conn.SendRequest(new ModifyRequest(groupDn, mod));
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD AssignGroupOwner failed (tenant={TenantId}, group={Group})", _tenantId, groupExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Resolve a free-form external id (DN, objectGUID, sAMAccountName, UPN)
    /// to an actual DN string. Mirrors the lookup chain in UpsertUser.
    /// </summary>
    private string? ResolveToDn(LdapConnection conn, string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        if (LooksLikeDn(externalId)) return DnExists(conn, externalId) ? externalId : null;
        // Try GUID first (32 hex chars or {GUID} form), then sAMAccountName, then UPN.
        if (Guid.TryParse(externalId, out _))
        {
            var byGuid = FindDnByGuid(conn, externalId);
            if (byGuid is not null) return byGuid;
        }
        var bySam = FindDnByFilter(conn, $"(sAMAccountName={EscapeLdapFilter(externalId)})");
        if (bySam is not null) return bySam;
        var byUpn = FindDnByFilter(conn, $"(userPrincipalName={EscapeLdapFilter(externalId)})");
        if (byUpn is not null) return byUpn;
        var byMail = FindDnByFilter(conn, $"(mail={EscapeLdapFilter(externalId)})");
        return byMail;
    }

    private static string EscapeLdapFilter(string s)
    {
        // RFC 4515 minimal — escape * ( ) \ NUL
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\\': sb.Append(@"\5c"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ─── LDAP helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Binds an LDAP connection and reports whether the channel is secure
    /// (LDAPS on :636 or successfully negotiated StartTLS on :389). Callers
    /// use the secure flag to gate unicodePwd writes.
    /// </summary>
    private LdapConnection Bind(string? domain, AdCredentials creds, out bool isSecure)
    {
        var (host, port) = ParseHostPort(domain);
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic
        };
        connection.SessionOptions.ProtocolVersion = 3;
        isSecure = false;

        if (port == 636)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            isSecure = true;
        }
        else
        {
            // Best-effort StartTLS upgrade on :389. If the server doesn't support
            // it we stay plaintext — and any password-write attempt will throw
            // when it gets to RequireSecureForPasswordWrite.
            try
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
                isSecure = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "StartTLS not available on {Host}:{Port} — connection will be plaintext. " +
                    "Password writes will be refused.", host, port);
            }
        }

        connection.Credential = new NetworkCredential(creds.Username, creds.Password);
        connection.Bind();
        return connection;
    }

    private static (string Host, int Port) ParseHostPort(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Tenant.Domain is required for AD connections (host or host:port).");
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }

    /// <summary>
    /// Resolution order: operator-supplied targetOU / ou attribute on the
    /// inbound object wins. Then the project scope's BaseDN (passed in via
    /// constructor). Then a RootDSE lookup for defaultNamingContext (with
    /// rootDomainNamingContext fallback). Mirrors IC's behavior — never
    /// silently invents CN=Users,&lt;domain&gt;.
    /// </summary>
    private string? ResolveBaseDn(LdapConnection conn, ConnectorObject obj)
    {
        var explicitOu = GetStr(obj, "targetOU") ?? GetStr(obj, "ou");
        if (!string.IsNullOrWhiteSpace(explicitOu)) return explicitOu;
        if (!string.IsNullOrWhiteSpace(_defaultBaseDn)) return _defaultBaseDn;
        return QueryRootDseDefaultNamingContext(conn);
    }

    /// <summary>
    /// RFC 4511 RootDSE search — empty DN, Base scope, "(objectClass=*)". We
    /// already have an authenticated connection, so no second anonymous bind
    /// needed (IC's helper had to do that because its discovery path runs
    /// before authentication). Returns the DN string or null on any failure.
    /// </summary>
    private string? QueryRootDseDefaultNamingContext(LdapConnection conn)
    {
        try
        {
            var req = new SearchRequest(
                distinguishedName: string.Empty,
                ldapFilter: "(objectClass=*)",
                searchScope: SearchScope.Base,
                attributeList: new[] { "defaultNamingContext", "rootDomainNamingContext" });
            var resp = (SearchResponse)conn.SendRequest(req);
            if (resp.Entries.Count == 0) return null;
            var attrs = resp.Entries[0].Attributes;
            if (attrs.Contains("defaultNamingContext"))
            {
                var v = attrs["defaultNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    _logger.LogInformation("RootDSE defaultNamingContext resolved as default Base DN: {Dn}", v);
                    return v;
                }
            }
            if (attrs.Contains("rootDomainNamingContext"))
            {
                var v = attrs["rootDomainNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    _logger.LogInformation("RootDSE rootDomainNamingContext resolved as default Base DN: {Dn}", v);
                    return v;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RootDSE lookup failed; no default Base DN available.");
            return null;
        }
    }

    private static bool DnExists(LdapConnection conn, string dn)
    {
        try
        {
            var req = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, new[] { "1.1" });
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return false;
        }
    }

    private static string? FindDnByGuid(LdapConnection conn, string guid)
    {
        // <GUID=...> form needs the binary GUID encoded as escaped hex bytes.
        if (!Guid.TryParse(guid, out var g)) return null;
        var hex = string.Join(string.Empty, g.ToByteArray().Select(b => $"\\{b:X2}"));
        // Subtree search needs a base; AD's RootDSE search at "" with a GUID filter works.
        var req = new SearchRequest("", $"(objectGUID={hex})", SearchScope.Subtree, new[] { "distinguishedName" });
        try
        {
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0].DistinguishedName : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDnByFilter(LdapConnection conn, string filter)
    {
        var req = new SearchRequest("", filter, SearchScope.Subtree, new[] { "distinguishedName" });
        try
        {
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0].DistinguishedName : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hard guard. AD refuses unicodePwd over plaintext anyway, but we throw
    /// BEFORE the wire attempt so the operator gets a friendly error instead
    /// of a confusing 'constraintViolation' deep in the directory log.
    /// </summary>
    private static void RequireSecureForPasswordWrite(bool isSecure)
    {
        if (!isSecure)
            throw new InvalidOperationException(
                "Refusing to write 'unicodePwd' over a plaintext LDAP channel. " +
                "AD requires LDAPS (port 636) or a successful StartTLS upgrade. " +
                "Reconfigure the Connected System's Domain to host:636, or enable StartTLS on the AD side.");
    }

    private static void SetPassword(LdapConnection conn, string dn, string password)
    {
        var quoted = $"\"{password}\"";
        var bytes = Encoding.Unicode.GetBytes(quoted);
        var mod = new DirectoryAttributeModification
        {
            Name = "unicodePwd",
            Operation = DirectoryAttributeOperation.Replace
        };
        mod.Add(bytes);
        conn.SendRequest(new ModifyRequest(dn, mod));
    }

    private static void SetUacFlag(LdapConnection conn, string dn, int flag, bool set)
    {
        var req = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, new[] { "userAccountControl" });
        var resp = (SearchResponse)conn.SendRequest(req);
        if (resp.Entries.Count == 0) return;
        var attrs = resp.Entries[0].Attributes;
        if (!attrs.Contains("userAccountControl")) return;
        var current = int.Parse(attrs["userAccountControl"][0]!.ToString()!);
        var updated = set ? current | flag : current & ~flag;
        if (updated == current) return;

        var mod = new DirectoryAttributeModification
        {
            Name = "userAccountControl",
            Operation = DirectoryAttributeOperation.Replace
        };
        mod.Add(updated.ToString());
        conn.SendRequest(new ModifyRequest(dn, mod));
    }

    private static void AppendIfPresent(AddRequest add, ConnectorObject obj, params string[] keys)
    {
        var (name, value) = FirstPresent(obj, keys);
        if (value is not null)
        {
            add.Attributes.Add(new DirectoryAttribute(name, value));
        }
    }

    private static void AddReplace(List<DirectoryAttributeModification> mods, ConnectorObject obj, params string[] keys)
    {
        var (name, value) = FirstPresent(obj, keys);
        if (value is null) return;
        var mod = new DirectoryAttributeModification
        {
            Name = name,
            Operation = DirectoryAttributeOperation.Replace
        };
        mod.Add(value);
        mods.Add(mod);
    }

    /// <summary>
    /// Full-replace of the 'member' attribute. Accepts members / member / Members
    /// keys; values may be a string list or {value,type} dicts. If the orchestrator
    /// didn't include a members key at all, we leave the existing membership
    /// untouched (so per-attribute mapping projects that don't care about groups
    /// don't accidentally clear them).
    /// </summary>
    private static void ReplaceMembers(List<DirectoryAttributeModification> mods, ConnectorObject obj)
    {
        var dns = ExtractMemberDns(obj);
        if (dns is null) return; // no key supplied → don't touch
        var mod = new DirectoryAttributeModification
        {
            Name = "member",
            Operation = DirectoryAttributeOperation.Replace
        };
        foreach (var dn in dns) mod.Add(dn);
        mods.Add(mod);
    }

    private static void AppendMembersOnCreate(AddRequest add, ConnectorObject obj)
    {
        var dns = ExtractMemberDns(obj);
        if (dns is null || dns.Count == 0) return;
        add.Attributes.Add(new DirectoryAttribute("member", dns.ToArray()));
    }

    private static List<string>? ExtractMemberDns(ConnectorObject obj)
    {
        object? raw = null;
        foreach (var key in new[] { "member", "members", "Members" })
        {
            if (obj.Attributes.TryGetValue(key, out var v) && v is not null) { raw = v; break; }
        }
        if (raw is null) return null;
        var result = new List<string>();
        if (raw is string s && !string.IsNullOrWhiteSpace(s)) { result.Add(s); return result; }
        if (raw is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is null) continue;
                if (item is string str) { if (!string.IsNullOrWhiteSpace(str)) result.Add(str); continue; }
                if (item is System.Collections.IDictionary dict && dict["value"] is string dv && !string.IsNullOrWhiteSpace(dv))
                {
                    result.Add(dv); continue;
                }
                var asString = item.ToString();
                if (!string.IsNullOrWhiteSpace(asString)) result.Add(asString!);
            }
        }
        return result;
    }

    private static (string Name, string? Value) FirstPresent(ConnectorObject obj, string[] keys)
    {
        // First key wins as the canonical AD attribute name; remaining keys are
        // fallbacks the orchestrator/mapping layer might have used.
        foreach (var k in keys)
        {
            if (obj.Attributes.TryGetValue(k, out var v) && v is not null)
            {
                var s = v switch
                {
                    string str => str,
                    System.Collections.IList list when list.Count > 0 => list[0]?.ToString(),
                    _ => v.ToString()
                };
                if (!string.IsNullOrEmpty(s)) return (keys[0], s);
            }
        }
        return (keys[0], null);
    }

    // ─── credentials + parse helpers ───────────────────────────────────────

    private sealed record AdCredentials(string Username, string Password);

    private async Task<AdCredentials?> ReadCredsAsync()
    {
        var name = Conduit.Sync.Security.CredentialNameContext.Resolve("ldap", Conduit.Sync.Security.CredentialSide.Sink);
        var raw = await _protector.RetrieveAsync(_tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var srcName = Conduit.Sync.Security.CredentialNameContext.Resolve("ldap", Conduit.Sync.Security.CredentialSide.Source);
            if (!string.Equals(srcName, name, StringComparison.OrdinalIgnoreCase))
                raw = await _protector.RetrieveAsync(_tenantId, srcName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null) return null;
            return new AdCredentials(u, p);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStr(ConnectorObject obj, string key)
    {
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is System.Collections.IList list && list.Count > 0) return list[0]?.ToString();
        return v.ToString();
    }

    private static bool TryGetBool(ConnectorObject obj, string key, out bool value)
    {
        value = false;
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return false;
        if (v is bool b) { value = b; return true; }
        if (bool.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
        return false;
    }

    private static bool LooksLikeDn(string s) =>
        !string.IsNullOrEmpty(s) && s.Contains('=', StringComparison.Ordinal) && s.Contains(',', StringComparison.Ordinal);

    private static string EscapeFilter(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*':  sb.Append(@"\2a"); break;
                case '(':  sb.Append(@"\28"); break;
                case ')':  sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string EscapeDnComponent(string s)
    {
        // RFC 4514 — escape reserved characters in a DN component value.
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (",+\"\\<>;=".IndexOf(c) >= 0) sb.Append('\\');
            if (c == '#' && i == 0) sb.Append('\\');
            if (c == ' ' && (i == 0 || i == s.Length - 1)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
