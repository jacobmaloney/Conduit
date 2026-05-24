using System;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GoogleWorkspace;

/// <summary>
/// Google Workspace sink. Users:
///   - lookup by SourceId (id or primaryEmail).
///   - create: Users.Insert (random password, requires reset on first sign-in).
///   - update: Users.Update.
///   - delete: Users.Delete.
/// Groups:
///   - lookup by SourceId.
///   - create: Groups.Insert.
///   - update: Groups.Update.
/// </summary>
public sealed class GoogleWorkspaceSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<GoogleWorkspaceSink> _logger;

    public GoogleWorkspaceSink(Guid tenantId, CredentialProtector protector, ILogger<GoogleWorkspaceSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'google' credential for tenant {_tenantId}.");
            var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: false);
            return string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? await UpsertGroupAsync(service, obj, cancellationToken)
                : await UpsertUserAsync(service, obj, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'google' credential for tenant {_tenantId}.");
            var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: false);
            try { await service.Users.Delete(sourceId).ExecuteAsync(cancellationToken); return SinkWriteResult.Ok(SinkWriteOutcome.Updated); }
            catch { /* try as group */ }
            try { await service.Groups.Delete(sourceId).ExecuteAsync(cancellationToken); return SinkWriteResult.Ok(SinkWriteOutcome.Updated); }
            catch { return SinkWriteResult.Ok(SinkWriteOutcome.Skipped); }
        }
        catch (Exception ex)
        {
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'google' credential stored." };
            var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: false);
            var req = service.Users.List();
            if (!string.IsNullOrEmpty(creds.Domain)) req.Domain = creds.Domain;
            else req.Customer = "my_customer";
            req.MaxResults = 1;
            await req.ExecuteAsync(cancellationToken);
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Google sink ready (admin {creds.AdminEmail})." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── Phase 8: AssignManager ─────────────────────────────────────────────
    //
    // Google Workspace stores the manager link in the user's `relations[]`
    // collection with type="manager" + value=<manager email>. Matches IC's
    // read pattern in GoogleWorkspaceQueryService (Relations loop reading
    // type=="manager" → value). We GET the user, mutate the relations list
    // (remove any existing manager entry, add the new one), then Update.

    public async Task<SinkWriteResult> AssignManagerAsync(string userExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userExternalId))
                return SinkWriteResult.Fail("AssignManager: userExternalId is empty.");
            if (string.IsNullOrWhiteSpace(managerExternalId))
                return SinkWriteResult.Fail("AssignManager: managerExternalId is empty.");

            var creds = await GoogleCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'google' credential for tenant {_tenantId}.");
            var service = await GoogleCredentialReader.CreateServiceAsync(creds, readOnly: false);

            // Manager value must be the manager's primary email. Caller may pass
            // the email directly or a Google user id; resolve via Users.Get when
            // it doesn't look like an email.
            string managerEmail = managerExternalId;
            if (!managerExternalId.Contains('@'))
            {
                try
                {
                    var mgr = await service.Users.Get(managerExternalId).ExecuteAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(mgr?.PrimaryEmail)) managerEmail = mgr.PrimaryEmail;
                }
                catch { /* fall through with original value */ }
            }

            // Fetch user. Accept id or primaryEmail per Users.Get's contract.
            User? user;
            try
            {
                user = await service.Users.Get(userExternalId).ExecuteAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return SinkWriteResult.Fail($"AssignManager: target user '{userExternalId}' not found in Google ({ex.Message}).");
            }
            if (user is null)
                return SinkWriteResult.Fail($"AssignManager: target user '{userExternalId}' not found in Google.");

            // user.Relations is `object` in the SDK — it deserializes as a JArray /
            // Newtonsoft JToken at runtime. Build a fresh List<UserRelation> with
            // any non-manager rows preserved + one manager row stamped.
            var newRelations = new List<UserRelation>();
            if (user.Relations is System.Collections.IEnumerable existing)
            {
                foreach (var item in existing)
                {
                    if (item is null) continue;
                    string? type = null;
                    string? value = null;
                    string? customType = null;
                    // Walk reflectively so we work whether the SDK gave us UserRelation
                    // instances or untyped JToken objects.
                    var t = item.GetType();
                    var typeProp = t.GetProperty("Type");
                    var valueProp = t.GetProperty("Value");
                    var customTypeProp = t.GetProperty("CustomType");
                    if (typeProp is not null) type = typeProp.GetValue(item)?.ToString();
                    if (valueProp is not null) value = valueProp.GetValue(item)?.ToString();
                    if (customTypeProp is not null) customType = customTypeProp.GetValue(item)?.ToString();
                    // Fallback for JToken-shaped entries: index via ToString JSON path.
                    if (type is null)
                    {
                        var idxer = t.GetMethod("get_Item", new[] { typeof(string) });
                        if (idxer is not null)
                        {
                            try { type = idxer.Invoke(item, new object[] { "type" })?.ToString(); } catch { }
                            try { value = idxer.Invoke(item, new object[] { "value" })?.ToString(); } catch { }
                            try { customType = idxer.Invoke(item, new object[] { "customType" })?.ToString(); } catch { }
                        }
                    }
                    if (string.Equals(type, "manager", StringComparison.OrdinalIgnoreCase)) continue;
                    newRelations.Add(new UserRelation
                    {
                        Type = type,
                        Value = value,
                        CustomType = customType
                    });
                }
            }
            newRelations.Add(new UserRelation { Type = "manager", Value = managerEmail });

            // Patch the user with relations only. Use a minimal User payload so
            // Update doesn't clobber unrelated fields (Google's API is full-replace
            // on top-level objects but tolerates sparse top-level payloads).
            var patch = new User
            {
                Relations = newRelations
            };
            await service.Users.Update(patch, user.Id ?? userExternalId).ExecuteAsync(cancellationToken);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google AssignManager failed for {UserId}", userExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private async Task<SinkWriteResult> UpsertUserAsync(DirectoryService service, ConnectorObject obj, CancellationToken ct)
    {
        var email = Get(obj, "email") ?? Get(obj, "mail") ?? Get(obj, "userPrincipalName");
        if (string.IsNullOrWhiteSpace(email))
            return SinkWriteResult.Fail("Cannot upsert Google user without primary email.");

        var existing = await TryGetUserAsync(service, obj.SourceId, email, ct);
        var user = existing ?? new User
        {
            PrimaryEmail = email,
            Password = Guid.NewGuid().ToString("N") + "Aa1!",
            ChangePasswordAtNextLogin = true,
            Name = new UserName()
        };

        user.Name ??= new UserName();
        if (TryGet(obj, "givenName", out var gn)) user.Name.GivenName = gn;
        if (TryGet(obj, "surname", out var sn)) user.Name.FamilyName = sn;
        else if (TryGet(obj, "sn", out var sn2)) user.Name.FamilyName = sn2;
        else if (TryGet(obj, "familyName", out var fn)) user.Name.FamilyName = fn;
        if (TryGet(obj, "displayName", out var dn)) user.Name.FullName = dn;

        if (existing is null)
        {
            user.PrimaryEmail = email;
        }
        if (obj.Attributes.TryGetValue("accountEnabled", out var ae) && ae is bool aeb) user.Suspended = !aeb;
        else if (obj.Attributes.TryGetValue("active", out var act) && act is bool actb) user.Suspended = !actb;

        if (TryGet(obj, "orgUnitPath", out var ou)) user.OrgUnitPath = ou;

        if (existing is null)
        {
            await service.Users.Insert(user).ExecuteAsync(ct);
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        await service.Users.Update(user, existing.Id ?? email).ExecuteAsync(ct);
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private async Task<SinkWriteResult> UpsertGroupAsync(DirectoryService service, ConnectorObject obj, CancellationToken ct)
    {
        var name = Get(obj, "displayName") ?? Get(obj, "cn") ?? Get(obj, "name");
        var email = Get(obj, "email") ?? Get(obj, "mail");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            return SinkWriteResult.Fail("Cannot upsert Google group without name + email.");

        var existing = await TryGetGroupAsync(service, obj.SourceId, email, ct);
        var group = existing ?? new Group
        {
            Email = email,
            Name = name
        };
        group.Name = name;
        group.Description = Get(obj, "description");

        if (existing is null)
        {
            group.Email = email;
            await service.Groups.Insert(group).ExecuteAsync(ct);
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        await service.Groups.Update(group, existing.Id ?? email).ExecuteAsync(ct);
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static async Task<User?> TryGetUserAsync(DirectoryService service, string? id, string email, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return await service.Users.Get(id).ExecuteAsync(ct); } catch { /* try email */ }
        }
        try { return await service.Users.Get(email).ExecuteAsync(ct); } catch { return null; }
    }

    private static async Task<Group?> TryGetGroupAsync(DirectoryService service, string? id, string email, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return await service.Groups.Get(id).ExecuteAsync(ct); } catch { /* try email */ }
        }
        try { return await service.Groups.Get(email).ExecuteAsync(ct); } catch { return null; }
    }

    private static string? Get(ConnectorObject obj, string key) =>
        obj.Attributes.TryGetValue(key, out var v) && v is not null
            ? (v is string s ? (string.IsNullOrEmpty(s) ? null : s) : v.ToString())
            : null;

    private static bool TryGet(ConnectorObject obj, string key, out string value)
    {
        value = Get(obj, key) ?? string.Empty;
        return value.Length > 0;
    }
}
