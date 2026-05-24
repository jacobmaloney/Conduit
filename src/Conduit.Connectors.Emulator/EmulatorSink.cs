using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Conduit.DataAccess;
using Conduit.Sync.Connectors;

namespace Conduit.Connectors.Emulator;

/// <summary>
/// Writes ConnectorObjects into the Conduit-owned Users/Groups tables under
/// the sink TenantId. ExternalId = ConnectorObject.SourceId is the lookup
/// key — orchestrator-mapped attributes provide the rest. This bypasses the
/// scoped-tenant-context UserRepository deliberately; the sink writes ARE the
/// privileged path and we want to target a specific tenant by id.
///
/// Phase 1B adds Group support — upserts into Groups + GroupMembers. Member
/// references arrive as ConnectorObject.Attributes["members"] (string list of
/// member externalIds OR SCIM-style { value, type } objects). Members are
/// resolved against Users by (TenantId, ExternalId).
/// </summary>
public sealed class EmulatorSink : IConnectorSink
{
    private readonly Guid _sinkTenantId;
    private readonly DatabaseConfig _config;
    private readonly ILogger<EmulatorSink> _logger;

    public EmulatorSink(Guid sinkTenantId, DatabaseConfig config, ILogger<EmulatorSink> logger)
    {
        _sinkTenantId = sinkTenantId;
        _config = config;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        if (string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase))
        {
            return await UpsertGroupAsync(obj, cancellationToken);
        }

        if (!string.Equals(obj.ObjectClass, "User", StringComparison.OrdinalIgnoreCase))
        {
            return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
        }

        // Pull mapped attributes. The orchestrator has already applied the
        // AttributeMapping table — so attrs are keyed by the SINK side name.
        string? userName       = GetStr(obj, "userName")
                              ?? GetStr(obj, "UserName")
                              ?? GetStr(obj, "sAMAccountName");
        string? displayName    = GetStr(obj, "displayName") ?? GetStr(obj, "DisplayName");
        string? givenName      = GetStr(obj, "givenName")   ?? GetStr(obj, "GivenName");
        string? familyName     = GetStr(obj, "familyName")  ?? GetStr(obj, "FamilyName") ?? GetStr(obj, "sn");
        string? email          = GetStr(obj, "email")       ?? GetStr(obj, "mail");
        string? title          = GetStr(obj, "title");
        string? department     = GetStr(obj, "department");
        string? employeeNumber = GetStr(obj, "employeeNumber") ?? GetStr(obj, "employeeID");
        bool   active          = !TryGetBool(obj, "active", out var a) || a;

        if (string.IsNullOrWhiteSpace(userName))
        {
            return SinkWriteResult.Fail("ConnectorObject is missing a userName / sAMAccountName attribute after mapping.");
        }

        try
        {
            using var conn = new SqlConnection(_config.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            // Lookup by (TenantId, ExternalId) first, then (TenantId, UserName) — both are
            // legitimate keys depending on what the source provided.
            var existingId = await conn.ExecuteScalarAsync<Guid?>(@"
                SELECT TOP 1 Id FROM Users
                 WHERE TenantId = @TenantId
                   AND (ExternalId = @ExternalId OR (ExternalId IS NULL AND UserName = @UserName))",
                new { TenantId = _sinkTenantId, ExternalId = obj.SourceId, UserName = userName });

            if (existingId is null)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO Users
                        (Id, TenantId, ExternalId, UserName, Active, Created, LastModified, Version,
                         DisplayName, FamilyName, GivenName, Title, Department, EmployeeNumber)
                    VALUES
                        (NEWID(), @TenantId, @ExternalId, @UserName, @Active, SYSUTCDATETIME(), SYSUTCDATETIME(), 1,
                         @DisplayName, @FamilyName, @GivenName, @Title, @Department, @EmployeeNumber);",
                    new
                    {
                        TenantId = _sinkTenantId,
                        ExternalId = obj.SourceId,
                        UserName = userName,
                        Active = active,
                        DisplayName = displayName,
                        FamilyName = familyName,
                        GivenName = givenName,
                        Title = title,
                        Department = department,
                        EmployeeNumber = employeeNumber
                    });

                if (!string.IsNullOrEmpty(email))
                {
                    var newId = await conn.ExecuteScalarAsync<Guid>(
                        "SELECT Id FROM Users WHERE TenantId = @TenantId AND ExternalId = @ExternalId",
                        new { TenantId = _sinkTenantId, ExternalId = obj.SourceId });
                    await UpsertPrimaryEmailAsync(conn, newId, email);
                }
                return SinkWriteResult.Ok(SinkWriteOutcome.Created);
            }
            else
            {
                await conn.ExecuteAsync(@"
                    UPDATE Users
                       SET UserName = @UserName,
                           Active = @Active,
                           DisplayName = COALESCE(@DisplayName, DisplayName),
                           FamilyName = COALESCE(@FamilyName, FamilyName),
                           GivenName = COALESCE(@GivenName, GivenName),
                           Title = COALESCE(@Title, Title),
                           Department = COALESCE(@Department, Department),
                           EmployeeNumber = COALESCE(@EmployeeNumber, EmployeeNumber),
                           LastModified = SYSUTCDATETIME(),
                           Version = Version + 1
                     WHERE Id = @Id;",
                    new
                    {
                        Id = existingId.Value,
                        UserName = userName,
                        Active = active,
                        DisplayName = displayName,
                        FamilyName = familyName,
                        GivenName = givenName,
                        Title = title,
                        Department = department,
                        EmployeeNumber = employeeNumber
                    });

                if (!string.IsNullOrEmpty(email))
                    await UpsertPrimaryEmailAsync(conn, existingId.Value, email);

                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emulator sink upsert failed for SourceId={SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private async Task<SinkWriteResult> UpsertGroupAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        string? displayName = GetStr(obj, "displayName") ?? GetStr(obj, "DisplayName") ?? GetStr(obj, "cn");
        string? description = GetStr(obj, "description") ?? GetStr(obj, "Description");
        string? groupType   = GetStr(obj, "groupType")   ?? GetStr(obj, "Type") ?? "Security";

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return SinkWriteResult.Fail("ConnectorObject is missing a displayName attribute after mapping.");
        }

        try
        {
            using var conn = new SqlConnection(_config.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            var existingId = await conn.ExecuteScalarAsync<Guid?>(@"
                SELECT TOP 1 Id FROM Groups
                 WHERE TenantId = @TenantId
                   AND (ExternalId = @ExternalId OR (ExternalId IS NULL AND DisplayName = @DisplayName))",
                new { TenantId = _sinkTenantId, ExternalId = obj.SourceId, DisplayName = displayName });

            Guid groupId;
            SinkWriteOutcome outcome;

            if (existingId is null)
            {
                groupId = Guid.NewGuid();
                await conn.ExecuteAsync(@"
                    INSERT INTO Groups
                        (Id, TenantId, ExternalId, DisplayName, Description, Type, Created, LastModified, Version)
                    VALUES
                        (@Id, @TenantId, @ExternalId, @DisplayName, @Description, @Type, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);",
                    new
                    {
                        Id = groupId,
                        TenantId = _sinkTenantId,
                        ExternalId = obj.SourceId,
                        DisplayName = displayName,
                        Description = description,
                        Type = groupType
                    });
                outcome = SinkWriteOutcome.Created;
            }
            else
            {
                groupId = existingId.Value;
                await conn.ExecuteAsync(@"
                    UPDATE Groups
                       SET DisplayName = @DisplayName,
                           Description = COALESCE(@Description, Description),
                           Type = COALESCE(@Type, Type),
                           LastModified = SYSUTCDATETIME(),
                           Version = Version + 1
                     WHERE Id = @Id;",
                    new { Id = groupId, DisplayName = displayName, Description = description, Type = groupType });
                outcome = SinkWriteOutcome.Updated;
            }

            // Members: accept "members" or "member" as the bag — value list of
            // external ids (strings) or { value, type } objects. Resolve each to
            // a Users.Id within the same tenant and replace the membership set.
            var memberExternalIds = ExtractMemberExternalIds(obj);
            if (memberExternalIds.Count > 0)
            {
                var memberUserIds = (await conn.QueryAsync<Guid>(@"
                    SELECT Id FROM Users
                     WHERE TenantId = @TenantId
                       AND ExternalId IN @ExternalIds",
                    new { TenantId = _sinkTenantId, ExternalIds = memberExternalIds })).ToList();

                if (memberUserIds.Count > 0)
                {
                    // Delete existing membership for this group (full replace).
                    await conn.ExecuteAsync(
                        "DELETE FROM GroupMembers WHERE GroupId = @GroupId",
                        new { GroupId = groupId });

                    foreach (var uid in memberUserIds)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO GroupMembers (Id, GroupId, Value, [Type], [Primary])
                            VALUES (NEWID(), @GroupId, @Value, 'User', 0);",
                            new { GroupId = groupId, Value = uid });
                    }
                }
            }

            return SinkWriteResult.Ok(outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emulator sink group upsert failed for SourceId={SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private static List<string> ExtractMemberExternalIds(ConnectorObject obj)
    {
        var ids = new List<string>();
        foreach (var key in new[] { "members", "member", "Members" })
        {
            if (!obj.Attributes.TryGetValue(key, out var raw) || raw is null) continue;

            if (raw is string single)
            {
                ids.Add(single);
                continue;
            }
            if (raw is System.Collections.IEnumerable list)
            {
                foreach (var item in list)
                {
                    if (item is null) continue;
                    if (item is string s) { ids.Add(s); continue; }
                    // Reflection-light access for { value, ... } shapes.
                    var t = item.GetType();
                    var prop = t.GetProperty("value") ?? t.GetProperty("Value");
                    if (prop?.GetValue(item) is { } v) { ids.Add(v.ToString() ?? string.Empty); continue; }
                    ids.Add(item.ToString() ?? string.Empty);
                }
            }
        }
        return ids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var conn = new SqlConnection(_config.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Tenants WHERE Id = @Id",
                new { Id = _sinkTenantId });
            return count == 1
                ? new ConnectorTestResult { IsSuccessful = true, Message = "Sink tenant reachable." }
                : new ConnectorTestResult { IsSuccessful = false, Message = "Sink tenant not found." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private static async Task UpsertPrimaryEmailAsync(SqlConnection conn, Guid userId, string email)
    {
        await conn.ExecuteAsync(@"
            IF EXISTS (SELECT 1 FROM UserEmails WHERE UserId = @UserId AND [Primary] = 1)
                UPDATE UserEmails SET Value = @Email WHERE UserId = @UserId AND [Primary] = 1;
            ELSE
                INSERT INTO UserEmails (Id, UserId, Value, [Type], [Primary])
                VALUES (NEWID(), @UserId, @Email, 'work', 1);",
            new { UserId = userId, Email = email });
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
}
