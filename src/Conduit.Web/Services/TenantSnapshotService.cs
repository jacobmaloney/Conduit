using System.Text.Json;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services;

/// <summary>
/// Builds a JSON snapshot of every user, group, and membership scoped to a
/// Connected System. Used by the destructive flows on /connected-systems so
/// the audit log row carries the full state of the data that was about to
/// disappear — enabling forensic recovery (or at least forensic answers) if
/// it turns out the wipe was a mistake.
///
/// The output is bounded by the size of the tenant, not the whole DB; for
/// the conference demo this is small (hundreds of rows max). For production
/// use against tenants with hundreds of thousands of users this should write
/// to blob storage instead of an audit-log column — left as a follow-up; see
/// the issues log.
/// </summary>
public class TenantSnapshotService
{
    private readonly UserRepository _users;
    private readonly GroupRepository _groups;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TenantSnapshotService(UserRepository users, GroupRepository groups)
    {
        _users = users;
        _groups = groups;
    }

    /// <summary>
    /// Builds the snapshot. Returns a JSON string suitable for storing in
    /// AuditLogs.OldValue. Caller should pass the resulting string into the
    /// audit emit alongside the action metadata.
    /// </summary>
    public async Task<string> BuildAsync(Guid tenantId)
    {
        var (users, _) = await _users.GetAllAsync(
            new Conduit.Core.Models.ScimQueryOptions { StartIndex = 1, Count = 100_000 },
            tenantIdOverride: tenantId);
        var (groups, _) = await _groups.GetAllAsync(
            new Conduit.Core.Models.ScimQueryOptions { StartIndex = 1, Count = 100_000 },
            tenantIdOverride: tenantId);

        var doc = new
        {
            snapshotVersion = 1,
            tenantId,
            takenAtUtc = DateTime.UtcNow,
            counts = new { users = users.Count, groups = groups.Count },
            users = users.Select(u => new
            {
                u.Id,
                u.UserName,
                u.DisplayName,
                u.Active,
                u.ExternalId,
                u.Title,
                Name = u.Name,
                Emails = u.Emails,
                PhoneNumbers = u.PhoneNumbers,
                Addresses = u.Addresses,
                EnterpriseExtension = u.EnterpriseExtension,
            }),
            groups = groups.Select(g => new
            {
                g.Id,
                g.DisplayName,
                g.Description,
                g.Type,
                Members = g.Members?.Select(m => new { m.Value, m.Display, m.Type }),
            })
        };

        return JsonSerializer.Serialize(doc, JsonOpts);
    }
}
