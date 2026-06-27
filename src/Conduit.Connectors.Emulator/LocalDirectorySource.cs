using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Emulator;

/// <summary>
/// Source side of the Local Directory connector. Reads this Conduit instance's own
/// Users / Groups (under the connection's tenant id) and yields canonical
/// <see cref="ConnectorObject"/>s — the SAME well-known attribute keys every sink
/// understands (userName / displayName / givenName / sn / mail / department /
/// employeeNumber / active for users; displayName / description / members for
/// groups), so a Local Directory → AD / Entra / IC sync needs no per-pair code.
///
/// SourceId = a user/group's ExternalId when set, else its local row Guid (so a
/// row created locally without an ExternalId is still addressable). Group members
/// are emitted on the "_members" / "members" attribute as a semicolon-joined list
/// of member external ids, matching the forward SCIM mapper + the Emulator sink's
/// member extractor.
/// </summary>
public sealed class LocalDirectorySource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly LocalDirectoryRepository _repository;
    private readonly ILogger<LocalDirectorySource> _logger;

    public LocalDirectorySource(Guid tenantId, LocalDirectoryRepository repository, ILogger<LocalDirectorySource> logger)
    {
        _tenantId = tenantId;
        _repository = repository;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var isGroup = string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase);
        var max = scope.MaxObjects ?? int.MaxValue;
        var emitted = 0;

        if (isGroup)
        {
            var groups = await _repository.GetGroupsAsync(_tenantId);
            foreach (var g in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (emitted >= max) yield break;
                var externalId = !string.IsNullOrEmpty(g.ExternalId) ? g.ExternalId! : g.Id.ToString();
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = "Group",
                    ["displayName"] = g.DisplayName,
                    ["cn"] = g.DisplayName,
                    ["description"] = g.Description,
                    ["groupType"] = g.Type,
                };
                var members = await _repository.GetGroupMemberExternalIdsAsync(g.Id);
                if (members.Count > 0)
                {
                    var joined = string.Join(";", members);
                    attrs["_members"] = joined;
                    attrs["members"] = joined;
                }
                emitted++;
                yield return new ConnectorObject { SourceId = externalId, ObjectClass = "Group", Attributes = attrs };
            }
            yield break;
        }

        var users = await _repository.GetUsersAsync(_tenantId);
        foreach (var u in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emitted >= max) yield break;
            var externalId = !string.IsNullOrEmpty(u.ExternalId) ? u.ExternalId! : u.Id.ToString();
            var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = "User",
                ["userName"] = u.UserName,
                ["sAMAccountName"] = u.UserName,
                ["displayName"] = u.DisplayName,
                ["givenName"] = u.GivenName,
                ["sn"] = u.FamilyName,
                ["title"] = u.Title,
                ["department"] = u.Department,
                ["employeeNumber"] = u.EmployeeNumber,
                ["employeeID"] = u.EmployeeNumber,
                ["active"] = u.Active,
                ["accountEnabled"] = u.Active,
            };
            if (!string.IsNullOrEmpty(u.Email))
            {
                attrs["mail"] = u.Email;
                attrs["email"] = u.Email;
                attrs["userPrincipalName"] = u.Email;
            }
            emitted++;
            yield return new ConnectorObject { SourceId = externalId, ObjectClass = "User", Attributes = attrs };
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var users = await _repository.GetUsersAsync(_tenantId);
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Local Directory reachable ({users.Count} user(s) in this connection)."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local Directory source test failed (tenant={TenantId})", _tenantId);
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }
}
