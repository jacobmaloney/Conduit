using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using GraphGroup = Microsoft.Graph.Models.Group;
using GraphUser = Microsoft.Graph.Models.User;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Microsoft Graph sink. Lookup-then-decide via SourceId (Graph id) — Created
/// objects are written with accountEnabled=true and a random initial
/// passwordProfile (forced-change). Updates use PATCH. Deletes call DELETE
/// (Entra retains for 30 days; restore via UI).
/// </summary>
public sealed class EntraIDSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<EntraIDSink> _logger;

    public EntraIDSink(Guid tenantId, CredentialProtector protector, ILogger<EntraIDSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            // Phase 3: handle _deleted=true marker emitted by delta source.
            if (IsDeleted(obj))
                return await DeleteAsync(obj.SourceId, cancellationToken);

            var client = await CreateClientAsync();
            if (string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase))
                return await UpsertGroupAsync(client, obj, cancellationToken);
            return await UpsertUserAsync(client, obj, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Entra ID sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Phase 3 bulk write via Graph $batch. Capabilities declares MaxBatchSize=20
    /// (Graph's hard limit). Strategy: for each object, attempt PATCH against
    /// SourceId (the Entra GUID). Records where SourceId is empty OR the PATCH
    /// returns 404 fall through to per-record UpsertAsync (which does lookup +
    /// create). This trades correctness for speed only on the happy path
    /// (already-synced records on subsequent runs).
    ///
    /// We rely on Graph's BatchRequestContentCollection helper which auto-chunks
    /// at 20 — but we already chunk at 20 in the orchestrator, so one collection
    /// = one round-trip.
    /// </summary>
    public async Task<IReadOnlyList<SinkWriteResult>> UpsertBatchAsync(
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        var results = new SinkWriteResult[batch.Count];
        if (batch.Count == 0) return results;

        var client = await CreateClientAsync();
        var batchContent = new BatchRequestContentCollection(client);
        var stepIds = new string?[batch.Count];
        var fallbackIndexes = new List<int>();

        for (int i = 0; i < batch.Count; i++)
        {
            var obj = batch[i];
            // Tombstone path — no batching, single-record delete.
            if (IsDeleted(obj))
            {
                fallbackIndexes.Add(i);
                continue;
            }
            // No source id (never synced before) → must lookup + create. Per-record path.
            if (string.IsNullOrWhiteSpace(obj.SourceId))
            {
                fallbackIndexes.Add(i);
                continue;
            }

            RequestInformation reqInfo;
            try
            {
                if (string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase))
                {
                    var patch = BuildGroupPatch(obj);
                    reqInfo = client.Groups[obj.SourceId].ToPatchRequestInformation(patch);
                }
                else
                {
                    var patch = BuildUserPatch(obj);
                    reqInfo = client.Users[obj.SourceId].ToPatchRequestInformation(patch);
                }
                stepIds[i] = await batchContent.AddBatchRequestStepAsync(reqInfo);
            }
            catch (Exception ex)
            {
                // Per-record path on any prep failure.
                _logger.LogWarning(ex, "Entra batch prep failed for {SourceId}, falling back to per-record", obj.SourceId);
                fallbackIndexes.Add(i);
            }
        }

        // Send the batch if anything was added.
        if (stepIds.Length > 0 && Array.Exists(stepIds, s => s is not null))
        {
            try
            {
                var responseCollection = await client.Batch.PostAsync(batchContent, cancellationToken);
                var codes = await responseCollection.GetResponsesStatusCodesAsync();
                for (int i = 0; i < batch.Count; i++)
                {
                    if (stepIds[i] is not { Length: > 0 } sid) continue;
                    if (!codes.TryGetValue(sid, out var status))
                    {
                        results[i] = SinkWriteResult.Fail("Graph $batch: missing step response");
                        continue;
                    }
                    var code = (int)status;
                    if (code >= 200 && code < 300)
                    {
                        results[i] = SinkWriteResult.Ok(SinkWriteOutcome.Updated);
                    }
                    else if (code == 404)
                    {
                        // Resource gone since last sync; create path.
                        fallbackIndexes.Add(i);
                    }
                    else
                    {
                        results[i] = SinkWriteResult.Fail($"Graph $batch HTTP {code}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Entra ID $batch POST failed; falling back to per-record for all");
                // Whole batch blew up — fallback everything we tried to batch.
                for (int i = 0; i < batch.Count; i++)
                    if (stepIds[i] is { Length: > 0 } && results[i] is null)
                        fallbackIndexes.Add(i);
            }
        }

        // Per-record fallback for: no-source-id records, tombstones, 404s, prep failures.
        foreach (var idx in fallbackIndexes)
        {
            if (results[idx] is not null) continue;
            results[idx] = await UpsertAsync(batch[idx], cancellationToken);
        }

        // Anything still null means we processed it in the batch successfully? Defensive.
        for (int i = 0; i < results.Length; i++)
            if (results[i] is null) results[i] = SinkWriteResult.Ok(SinkWriteOutcome.Skipped);

        return results;
    }

    private static bool IsDeleted(ConnectorObject obj) =>
        obj.Attributes.TryGetValue("_deleted", out var v) && v is bool b && b;

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var client = await CreateClientAsync();
            await client.Users[sourceId].DeleteAsync(cancellationToken: cancellationToken);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
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
            var creds = await EntraIDCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'entraid' credential stored." };
            var client = await CreateClientAsync();
            var probe = await client.Users.GetAsync(req =>
            {
                req.QueryParameters.Top = 1;
                req.QueryParameters.Select = new[] { "id" };
            }, cancellationToken);
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Entra ID sink reachable (tenant {creds.TenantId})."
            };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private async Task<SinkWriteResult> UpsertUserAsync(GraphServiceClient client, ConnectorObject obj, CancellationToken ct)
    {
        // Try to find by id; if not found, lookup by UPN; otherwise create.
        var upn = GetStr(obj, "userPrincipalName") ?? GetStr(obj, "upn");
        var displayName = GetStr(obj, "displayName");
        var mailNickname = GetStr(obj, "mailNickname")
                        ?? GetStr(obj, "sAMAccountName")
                        ?? GetStr(obj, "userName");

        string? existingId = await TryFindUserIdAsync(client, obj.SourceId, upn, ct);

        var patch = BuildUserPatch(obj);

        if (existingId is null)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return SinkWriteResult.Fail("Cannot create Entra user without userPrincipalName.");
            if (string.IsNullOrWhiteSpace(displayName))
                return SinkWriteResult.Fail("Cannot create Entra user without displayName.");

            patch.UserPrincipalName = upn;
            patch.DisplayName = displayName;
            patch.MailNickname = mailNickname ?? upn.Split('@')[0];
            patch.AccountEnabled = patch.AccountEnabled ?? true;
            patch.PasswordProfile = new PasswordProfile
            {
                ForceChangePasswordNextSignIn = true,
                Password = Guid.NewGuid().ToString("N") + "Aa1!"
            };
            var created = await client.Users.PostAsync(patch, cancellationToken: ct);
            return created is null
                ? SinkWriteResult.Fail("Graph returned null on user create.")
                : SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        await client.Users[existingId].PatchAsync(patch, cancellationToken: ct);
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static async Task<string?> TryFindUserIdAsync(GraphServiceClient client, string? id, string? upn, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var hit = await client.Users[id].GetAsync(req => req.QueryParameters.Select = new[] { "id" }, ct);
                if (hit is not null) return hit.Id;
            }
            catch { /* not found by id; try upn */ }
        }
        if (!string.IsNullOrEmpty(upn))
        {
            try
            {
                var hit = await client.Users[upn].GetAsync(req => req.QueryParameters.Select = new[] { "id" }, ct);
                if (hit is not null) return hit.Id;
            }
            catch { /* not found */ }
        }
        return null;
    }

    private static GraphUser BuildUserPatch(ConnectorObject obj)
    {
        var u = new GraphUser();
        if (TryGetStr(obj, "displayName", out var dn)) u.DisplayName = dn;
        if (TryGetStr(obj, "givenName", out var gn)) u.GivenName = gn;
        if (TryGetStr(obj, "surname", out var sn)) u.Surname = sn;
        else if (TryGetStr(obj, "sn", out var sn2)) u.Surname = sn2;
        else if (TryGetStr(obj, "familyName", out var fn)) u.Surname = fn;
        if (TryGetStr(obj, "jobTitle", out var jt)) u.JobTitle = jt;
        else if (TryGetStr(obj, "title", out var t)) u.JobTitle = t;
        if (TryGetStr(obj, "department", out var d)) u.Department = d;
        if (TryGetStr(obj, "companyName", out var cn)) u.CompanyName = cn;
        else if (TryGetStr(obj, "company", out var c)) u.CompanyName = c;
        if (TryGetStr(obj, "officeLocation", out var ol)) u.OfficeLocation = ol;
        if (TryGetStr(obj, "mobilePhone", out var mp)) u.MobilePhone = mp;
        else if (TryGetStr(obj, "mobile", out var m)) u.MobilePhone = m;
        if (TryGetStr(obj, "streetAddress", out var sa)) u.StreetAddress = sa;
        if (TryGetStr(obj, "city", out var city)) u.City = city;
        if (TryGetStr(obj, "state", out var st)) u.State = st;
        if (TryGetStr(obj, "postalCode", out var pc)) u.PostalCode = pc;
        if (TryGetStr(obj, "country", out var country)) u.Country = country;
        if (TryGetStr(obj, "employeeId", out var eid)) u.EmployeeId = eid;
        else if (TryGetStr(obj, "employeeNumber", out var en)) u.EmployeeId = en;
        if (TryGetStr(obj, "employeeType", out var et)) u.EmployeeType = et;
        if (TryGetStr(obj, "mail", out var mail)) u.Mail = mail;
        else if (TryGetStr(obj, "email", out var email)) u.Mail = email;
        if (obj.Attributes.TryGetValue("accountEnabled", out var ae) && ae is bool aeb) u.AccountEnabled = aeb;
        else if (obj.Attributes.TryGetValue("active", out var act) && act is bool actb) u.AccountEnabled = actb;
        return u;
    }

    private async Task<SinkWriteResult> UpsertGroupAsync(GraphServiceClient client, ConnectorObject obj, CancellationToken ct)
    {
        var displayName = GetStr(obj, "displayName");
        if (string.IsNullOrWhiteSpace(displayName))
            return SinkWriteResult.Fail("Cannot upsert Entra group without displayName.");
        var mailNickname = GetStr(obj, "mailNickname") ?? displayName.Replace(" ", string.Empty);

        string? existingId = null;
        if (!string.IsNullOrEmpty(obj.SourceId))
        {
            try
            {
                var hit = await client.Groups[obj.SourceId].GetAsync(req => req.QueryParameters.Select = new[] { "id" }, ct);
                if (hit is not null) existingId = hit.Id;
            }
            catch { /* not found by id; create */ }
        }

        if (existingId is null)
        {
            var grp = new GraphGroup
            {
                DisplayName = displayName,
                MailNickname = mailNickname,
                Description = GetStr(obj, "description"),
                MailEnabled = false,
                SecurityEnabled = true
            };
            var created = await client.Groups.PostAsync(grp, cancellationToken: ct);
            return created is null
                ? SinkWriteResult.Fail("Graph returned null on group create.")
                : SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        await client.Groups[existingId].PatchAsync(BuildGroupPatch(obj), cancellationToken: ct);
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static GraphGroup BuildGroupPatch(ConnectorObject obj)
    {
        var g = new GraphGroup();
        if (TryGetStr(obj, "displayName", out var dn)) g.DisplayName = dn;
        if (TryGetStr(obj, "description", out var d)) g.Description = d;
        if (TryGetStr(obj, "mail", out var m)) g.Mail = m;
        if (TryGetStr(obj, "mailNickname", out var mn)) g.MailNickname = mn;
        return g;
    }

    private static string? GetStr(ConnectorObject obj, string key)
    {
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return string.IsNullOrEmpty(s) ? null : s;
        return v.ToString();
    }

    private static bool TryGetStr(ConnectorObject obj, string key, out string value)
    {
        value = GetStr(obj, key) ?? string.Empty;
        return value.Length > 0;
    }

    private async Task<GraphServiceClient> CreateClientAsync()
    {
        var creds = await EntraIDCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'entraid' credential stored for tenant {_tenantId}.");
        var credential = new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    // ── Phase 7 person-aware step methods ───────────────────────────────────
    //
    // Entra is a directory, not a Person store, so MatchPerson / CreatePerson
    // stay as NotSupported (default). AssignManager and AssignGroupOwner map to
    // Graph's $ref endpoints — both expect the target user's id, both take
    // {"@odata.id": "https://graph.microsoft.com/v1.0/users/<id>"} JSON shape.
    // The Kiota client exposes these as Manager.Ref.PutAsync and Owners.Ref.PostAsync.

    public async Task<SinkWriteResult> AssignManagerAsync(string objectExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var client = await CreateClientAsync();
            // Resolve both ends to Graph object ids (callers may hand us UPN or id).
            var targetId = await ResolveUserIdAsync(client, objectExternalId, cancellationToken);
            if (targetId is null) return SinkWriteResult.Fail($"User '{objectExternalId}' not found in Entra.");
            var managerId = await ResolveUserIdAsync(client, managerExternalId, cancellationToken);
            if (managerId is null) return SinkWriteResult.Fail($"Manager '{managerExternalId}' not found in Entra.");

            var body = new Microsoft.Graph.Models.ReferenceUpdate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/users/{managerId}"
            };
            await client.Users[targetId].Manager.Ref.PutAsync(body, cancellationToken: cancellationToken);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Entra AssignManager failed (tenant={TenantId}, target={Target})", _tenantId, objectExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> AssignGroupOwnerAsync(string groupExternalId, string ownerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var client = await CreateClientAsync();
            var groupId = await ResolveGroupIdAsync(client, groupExternalId, cancellationToken);
            if (groupId is null) return SinkWriteResult.Fail($"Group '{groupExternalId}' not found in Entra.");
            var ownerId = await ResolveUserIdAsync(client, ownerExternalId, cancellationToken);
            if (ownerId is null) return SinkWriteResult.Fail($"Owner '{ownerExternalId}' not found in Entra.");

            var body = new Microsoft.Graph.Models.ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/users/{ownerId}"
            };
            await client.Groups[groupId].Owners.Ref.PostAsync(body, cancellationToken: cancellationToken);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Entra AssignGroupOwner failed (tenant={TenantId}, group={Group})", _tenantId, groupExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private static async Task<string?> ResolveUserIdAsync(GraphServiceClient client, string externalId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        if (Guid.TryParse(externalId, out _)) return externalId;
        // Try as UPN / mail.
        try
        {
            var user = await client.Users[externalId].GetAsync(cancellationToken: ct);
            return user?.Id;
        }
        catch { return null; }
    }

    // ─── Phase 5 provisioning step methods ──────────────────────────────────
    //
    // CreateAsync — Graph Users.PostAsync. MoveAsync stays NotSupported (Entra
    // has no container concept). ResetPasswordAsync — Graph user PATCH with
    // PasswordProfile (the older but universally supported reset path; the
    // newer Authentication.PasswordMethods.ResetPassword API requires elevated
    // privileges and an SSPR-enabled tenant).

    public async Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken cancellationToken)
    {
        try
        {
            var client = await CreateClientAsync();
            var upn = GetStr(newObject, "userPrincipalName") ?? GetStr(newObject, "upn");
            if (string.IsNullOrWhiteSpace(upn))
                return ProvisionResult.Failed("Cannot create Entra user without userPrincipalName.");
            var displayName = GetStr(newObject, "displayName");
            if (string.IsNullOrWhiteSpace(displayName))
                return ProvisionResult.Failed("Cannot create Entra user without displayName.");
            var mailNickname = GetStr(newObject, "mailNickname")
                            ?? GetStr(newObject, "sAMAccountName")
                            ?? GetStr(newObject, "userName")
                            ?? upn.Split('@')[0];

            // Refuse if a user with this UPN already exists — explicit create
            // semantics (matching AD sink behavior).
            try
            {
                var existing = await client.Users[upn].GetAsync(req => req.QueryParameters.Select = new[] { "id" }, cancellationToken);
                if (existing is not null)
                    return ProvisionResult.Failed($"Entra user with UPN='{upn}' already exists (id={existing.Id}).");
            }
            catch { /* not found is the desired path */ }

            var patch = BuildUserPatch(newObject);
            patch.UserPrincipalName = upn;
            patch.DisplayName = displayName;
            patch.MailNickname = mailNickname;
            patch.AccountEnabled = patch.AccountEnabled ?? true;

            // Initial password: use supplied 'password' if present, otherwise a
            // random one (force-change-on-next-signin in both cases). The Worker
            // typically provisions disabled then runs reset-password — but if a
            // caller wants a one-shot, supply 'password' on the request.
            var supplied = GetStr(newObject, "password");
            patch.PasswordProfile = new PasswordProfile
            {
                ForceChangePasswordNextSignIn = true,
                Password = supplied ?? (Guid.NewGuid().ToString("N") + "Aa1!")
            };

            var created = await client.Users.PostAsync(patch, cancellationToken: cancellationToken);
            if (created is null || string.IsNullOrEmpty(created.Id))
                return ProvisionResult.Failed("Graph returned null on user create.");
            return ProvisionResult.Success(externalId: created.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Entra CreateAsync failed (tenant={TenantId})", _tenantId);
            return ProvisionResult.Failed(ex.Message);
        }
    }

    // MoveAsync intentionally NOT overridden — defaults to NotSupportedException.
    // Entra has no OU concept; the controller will surface ProvisionOutcome.NotSupported.

    public async Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(newPassword))
                return ProvisionResult.Failed("ResetPasswordAsync requires a non-empty newPassword.");

            var client = await CreateClientAsync();
            var userId = await ResolveUserIdAsync(client, externalId, cancellationToken);
            if (userId is null) return ProvisionResult.Failed($"Entra user '{externalId}' not found.");

            var patch = new GraphUser
            {
                PasswordProfile = new PasswordProfile
                {
                    Password = newPassword,
                    ForceChangePasswordNextSignIn = requireChangeAtNextLogin
                }
            };
            await client.Users[userId].PatchAsync(patch, cancellationToken: cancellationToken);
            return ProvisionResult.Success(externalId: userId);
        }
        catch (Exception ex)
        {
            // Never include newPassword in the log payload.
            _logger.LogError(ex, "Entra ResetPasswordAsync failed (tenant={TenantId}, target={Target})", _tenantId, externalId);
            return ProvisionResult.Failed(ex.Message);
        }
    }

    private static async Task<string?> ResolveGroupIdAsync(GraphServiceClient client, string externalId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        if (Guid.TryParse(externalId, out _)) return externalId;
        // Fall back to displayName / mailNickname filter.
        try
        {
            var result = await client.Groups.GetAsync(rc =>
            {
                rc.QueryParameters.Filter = $"displayName eq '{externalId.Replace("'", "''")}' or mailNickname eq '{externalId.Replace("'", "''")}'";
                rc.QueryParameters.Top = 1;
            }, ct);
            return result?.Value?.FirstOrDefault()?.Id;
        }
        catch { return null; }
    }
}
