using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;

namespace Conduit.Connectors.Emulator;

/// <summary>
/// Writes ConnectorObjects into the Conduit-owned Users/Groups tables under
/// the sink TenantId. ExternalId = ConnectorObject.SourceId is the lookup
/// key — orchestrator-mapped attributes provide the rest. The privileged
/// per-tenant writes go through EmulatorSinkRepository (Conduit.DataAccess),
/// which deliberately targets a specific tenant by id rather than the
/// scoped-tenant-context UserRepository.
///
/// Phase 1B adds Group support — upserts into Groups + GroupMembers. Member
/// references arrive as ConnectorObject.Attributes["members"] (string list of
/// member externalIds OR SCIM-style { value, type } objects). Members are
/// resolved against Users by (TenantId, ExternalId).
/// </summary>
public sealed class EmulatorSink : IConnectorSink
{
    private readonly Guid _sinkTenantId;
    private readonly EmulatorSinkRepository _repository;
    private readonly ILogger<EmulatorSink> _logger;

    public EmulatorSink(Guid sinkTenantId, EmulatorSinkRepository repository, ILogger<EmulatorSink> logger)
    {
        _sinkTenantId = sinkTenantId;
        _repository = repository;
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
            // Lookup by (TenantId, ExternalId) first, then (TenantId, UserName) — both are
            // legitimate keys depending on what the source provided.
            var existingId = await _repository.FindUserIdAsync(_sinkTenantId, obj.SourceId, userName);

            if (existingId is null)
            {
                await _repository.InsertUserAsync(_sinkTenantId, obj.SourceId, userName, active,
                    displayName, familyName, givenName, title, department, employeeNumber);

                if (!string.IsNullOrEmpty(email))
                {
                    var newId = await _repository.GetUserIdByExternalIdAsync(_sinkTenantId, obj.SourceId);
                    await _repository.UpsertPrimaryEmailAsync(newId, email);
                }
                return SinkWriteResult.Ok(SinkWriteOutcome.Created);
            }
            else
            {
                await _repository.UpdateUserAsync(existingId.Value, userName, active,
                    displayName, familyName, givenName, title, department, employeeNumber);

                if (!string.IsNullOrEmpty(email))
                    await _repository.UpsertPrimaryEmailAsync(existingId.Value, email);

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
            var existingId = await _repository.FindGroupIdAsync(_sinkTenantId, obj.SourceId, displayName);

            Guid groupId;
            SinkWriteOutcome outcome;

            if (existingId is null)
            {
                groupId = Guid.NewGuid();
                await _repository.InsertGroupAsync(groupId, _sinkTenantId, obj.SourceId,
                    displayName, description, groupType);
                outcome = SinkWriteOutcome.Created;
            }
            else
            {
                groupId = existingId.Value;
                await _repository.UpdateGroupAsync(groupId, displayName, description, groupType);
                outcome = SinkWriteOutcome.Updated;
            }

            // Members: accept "members" or "member" as the bag — value list of
            // external ids (strings) or { value, type } objects. Resolve each to
            // a Users.Id within the same tenant and replace the membership set.
            var memberExternalIds = ExtractMemberExternalIds(obj);
            if (memberExternalIds.Count > 0)
            {
                var memberUserIds = await _repository.ResolveMemberUserIdsAsync(_sinkTenantId, memberExternalIds);

                if (memberUserIds.Count > 0)
                {
                    await _repository.ReplaceGroupMembersAsync(groupId, memberUserIds);
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
            var count = await _repository.CountTenantAsync(_sinkTenantId);
            return count == 1
                ? new ConnectorTestResult { IsSuccessful = true, Message = "Sink tenant reachable." }
                : new ConnectorTestResult { IsSuccessful = false, Message = "Sink tenant not found." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
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
}
