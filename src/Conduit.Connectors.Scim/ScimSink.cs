using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.Scim;

/// <summary>
/// SCIM 2.0 sink.
///   - Users: POST /Users to create, PUT /Users/{id} to update.
///   - Groups: POST /Groups, PUT /Groups/{id}.
///   - Deletes: DELETE /Users/{id} or /Groups/{id}.
/// Lookup-then-decide via SourceId; falls back to ?filter=userName eq "..." for
/// users. We emit standard RFC 7643 schemas plus the enterprise extension when
/// the inbound object carries those attrs.
/// </summary>
public sealed class ScimSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<ScimSink> _logger;

    public ScimSink(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<ScimSink> logger)
    {
        _tenantId = tenantId;
        _httpFactory = httpFactory;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'scim' credential for tenant {_tenantId}.");
            var client = ScimCredentialReader.BuildClient(_httpFactory, creds);
            return string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? await UpsertGroupAsync(client, creds, obj, cancellationToken)
                : await UpsertUserAsync(client, creds, obj, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SCIM sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Phase 2 bulk path — SCIM 2.0 /Bulk operation per RFC 7644 §3.7. POSTs a
    /// BulkRequest with N operations (create or replace) and parses the BulkResponse
    /// for per-operation outcomes. Falls back to the default per-record loop on
    /// any server-side rejection (some SCIM servers don't implement /Bulk).
    /// </summary>
    public async Task<IReadOnlyList<SinkWriteResult>> UpsertBatchAsync(
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return Array.Empty<SinkWriteResult>();

        var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId);
        if (creds is null)
        {
            var fail = SinkWriteResult.Fail($"No 'scim' credential for tenant {_tenantId}.");
            var arr = new SinkWriteResult[batch.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = fail;
            return arr;
        }
        var client = ScimCredentialReader.BuildClient(_httpFactory, creds);

        // Build BulkRequest.
        var ops = new List<object>(batch.Count);
        var operationTypes = new (string Path, string? Method, string BulkId)[batch.Count]; // for response correlation
        for (int i = 0; i < batch.Count; i++)
        {
            var obj = batch[i];
            var bulkId = $"op{i}";
            try
            {
                if (string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase))
                {
                    var displayName = Get(obj, "displayName") ?? Get(obj, "cn");
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        operationTypes[i] = (string.Empty, null, bulkId);
                        continue;
                    }
                    var members = ExtractMembers(obj);
                    var data = new Dictionary<string, object?>
                    {
                        ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
                        ["displayName"] = displayName,
                        ["externalId"] = Get(obj, "externalId"),
                        ["members"] = members
                    };
                    ops.Add(new
                    {
                        method = "POST",
                        path = creds.GroupEndpoint,
                        bulkId,
                        data
                    });
                    operationTypes[i] = (creds.GroupEndpoint, "POST", bulkId);
                }
                else
                {
                    var userName = Get(obj, "userName") ?? Get(obj, "userPrincipalName");
                    if (string.IsNullOrWhiteSpace(userName))
                    {
                        operationTypes[i] = (string.Empty, null, bulkId);
                        continue;
                    }
                    var payload = BuildUserPayload(obj, userName);
                    ops.Add(new
                    {
                        method = "POST",
                        path = creds.UserEndpoint,
                        bulkId,
                        data = payload
                    });
                    operationTypes[i] = (creds.UserEndpoint, "POST", bulkId);
                }
            }
            catch
            {
                operationTypes[i] = (string.Empty, null, bulkId);
            }
        }

        if (ops.Count == 0)
        {
            // Nothing to bulk — return individual failures.
            var arr = new SinkWriteResult[batch.Count];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = SinkWriteResult.Fail("Skipped: missing userName / displayName.");
            return arr;
        }

        var bulkPayload = new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:BulkRequest" },
            failOnErrors = 0,
            Operations = ops
        };

        HttpResponseMessage bulkResp;
        try
        {
            var json = JsonSerializer.Serialize(bulkPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/scim+json");
            bulkResp = await client.PostAsync($"{creds.BaseUrl}/Bulk", content, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SCIM /Bulk POST threw; falling back to per-record upsert.");
            return await FallbackPerRecordAsync(batch, cancellationToken);
        }

        if (!bulkResp.IsSuccessStatusCode)
        {
            // Some SCIM servers (Okta, Microsoft) don't honor /Bulk. Soft fallback.
            _logger.LogInformation("SCIM /Bulk returned HTTP {Code}; falling back to per-record.", (int)bulkResp.StatusCode);
            return await FallbackPerRecordAsync(batch, cancellationToken);
        }

        try
        {
            using var doc = JsonDocument.Parse(await bulkResp.Content.ReadAsStringAsync(cancellationToken));
            var byBulkId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("Operations", out var opsEl) && opsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var op in opsEl.EnumerateArray())
                {
                    var bid = op.TryGetProperty("bulkId", out var bidEl) ? bidEl.GetString() : null;
                    if (!string.IsNullOrEmpty(bid)) byBulkId[bid!] = op;
                }
            }
            var results = new SinkWriteResult[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                var (_, method, bulkId) = operationTypes[i];
                if (method is null) { results[i] = SinkWriteResult.Fail("Skipped: missing userName / displayName."); continue; }
                if (!byBulkId.TryGetValue(bulkId, out var opResp)) { results[i] = SinkWriteResult.Fail("No bulk response."); continue; }
                if (opResp.TryGetProperty("status", out var sEl))
                {
                    var code = sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : sEl.GetRawText();
                    if (int.TryParse(code, out var http) && http is >= 200 and < 300)
                        results[i] = SinkWriteResult.Ok(SinkWriteOutcome.Created);
                    else if (int.TryParse(code, out var http2) && http2 == 409)
                        results[i] = SinkWriteResult.Ok(SinkWriteOutcome.Updated); // already-exists path
                    else
                        results[i] = SinkWriteResult.Fail($"Bulk op returned status {code}.");
                }
                else
                {
                    results[i] = SinkWriteResult.Ok(SinkWriteOutcome.Created);
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not parse SCIM BulkResponse; falling back.");
            return await FallbackPerRecordAsync(batch, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<SinkWriteResult>> FallbackPerRecordAsync(
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken ct)
    {
        var results = new List<SinkWriteResult>(batch.Count);
        foreach (var obj in batch)
        {
            ct.ThrowIfCancellationRequested();
            try { results.Add(await UpsertAsync(obj, ct)); }
            catch (Exception ex) { results.Add(SinkWriteResult.Fail(ex.Message)); }
        }
        return results;
    }

    private static List<object> ExtractMembers(ConnectorObject obj)
    {
        var members = new List<object>();
        if (obj.Attributes.TryGetValue("members", out var memVal) && memVal is System.Collections.IEnumerable list)
        {
            foreach (var m in list)
            {
                if (m is null) continue;
                var s = m as string ?? m.ToString();
                if (string.IsNullOrEmpty(s)) continue;
                members.Add(new { value = s, type = "User" });
            }
        }
        return members;
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'scim' credential for tenant {_tenantId}.");
            var client = ScimCredentialReader.BuildClient(_httpFactory, creds);
            var del = await client.DeleteAsync($"{creds.BaseUrl}{creds.UserEndpoint}/{sourceId}", cancellationToken);
            if (del.IsSuccessStatusCode || del.StatusCode == HttpStatusCode.NotFound)
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            // Try group
            var gdel = await client.DeleteAsync($"{creds.BaseUrl}{creds.GroupEndpoint}/{sourceId}", cancellationToken);
            if (gdel.IsSuccessStatusCode || gdel.StatusCode == HttpStatusCode.NotFound)
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            return SinkWriteResult.Fail($"DELETE returned HTTP {(int)gdel.StatusCode}.");
        }
        catch (Exception ex)
        {
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var src = new ScimSource(_tenantId, _httpFactory, _protector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScimSource>.Instance);
        return await src.TestConnectionAsync(cancellationToken);
    }

    // ─── Phase 8: AssignManager ─────────────────────────────────────────────
    //
    // SCIM 2.0 enterprise extension stores manager as `manager.value` (an opaque
    // user id). PATCH /Users/{id} with the enterprise-User schema PatchOp is the
    // canonical write. Spec: RFC 7644 §3.5.2 + RFC 7643 §4.3. We resolve the
    // target user (id or userName) but pass the manager value through unchanged
    // — the SCIM server is the source of truth for its own user ids.

    public async Task<SinkWriteResult> AssignManagerAsync(string userExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userExternalId))
                return SinkWriteResult.Fail("AssignManager: userExternalId is empty.");
            if (string.IsNullOrWhiteSpace(managerExternalId))
                return SinkWriteResult.Fail("AssignManager: managerExternalId is empty.");

            var creds = await ScimCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'scim' credential for tenant {_tenantId}.");
            var client = ScimCredentialReader.BuildClient(_httpFactory, creds);

            var userId = await TryFindUserAsync(client, creds, userExternalId, userExternalId, cancellationToken);
            if (userId is null)
                return SinkWriteResult.Fail($"AssignManager: target user '{userExternalId}' not found in SCIM endpoint.");

            // Patch operations syntax per RFC 7644 §3.5.2. We use "add" (servers
            // treat it as upsert on a single-valued attribute) targeting the
            // enterprise-User extension's manager.value sub-attribute.
            const string enterpriseSchema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
            var patchBody = new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new object[]
                {
                    new
                    {
                        op = "add",
                        path = $"{enterpriseSchema}:manager",
                        value = new { value = managerExternalId }
                    }
                }
            };
            var json = JsonSerializer.Serialize(patchBody);
            var content = new StringContent(json, Encoding.UTF8, "application/scim+json");

            using var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                $"{creds.BaseUrl}{creds.UserEndpoint}/{userId}")
            { Content = content };
            var resp = await client.SendAsync(req, cancellationToken);

            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed || resp.StatusCode == HttpStatusCode.NotImplemented)
            {
                // Some SCIM servers reject PATCH. Soft-skip with a clear error so the
                // operator knows to fall back to a Mapping step that writes manager.
                return SinkWriteResult.Fail($"AssignManager: SCIM server returned {(int)resp.StatusCode} on PATCH — server may not support PatchOp. Consider mapping `manager` via a Mapping step instead.");
            }
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"AssignManager: PATCH returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(cancellationToken)}");

            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SCIM AssignManager failed for {UserId}", userExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private async Task<SinkWriteResult> UpsertUserAsync(HttpClient client, ScimCredentials creds, ConnectorObject obj, CancellationToken ct)
    {
        var userName = Get(obj, "userName") ?? Get(obj, "userPrincipalName");
        if (string.IsNullOrWhiteSpace(userName))
            return SinkWriteResult.Fail("Cannot upsert SCIM user without userName.");

        var existingId = await TryFindUserAsync(client, creds, obj.SourceId, userName, ct);
        var payload = BuildUserPayload(obj, userName);
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/scim+json");

        if (existingId is null)
        {
            var resp = await client.PostAsync($"{creds.BaseUrl}{creds.UserEndpoint}", content, ct);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"POST returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        var put = await client.PutAsync($"{creds.BaseUrl}{creds.UserEndpoint}/{existingId}", content, ct);
        if (!put.IsSuccessStatusCode)
            return SinkWriteResult.Fail($"PUT returned HTTP {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(ct)}");
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private async Task<SinkWriteResult> UpsertGroupAsync(HttpClient client, ScimCredentials creds, ConnectorObject obj, CancellationToken ct)
    {
        var displayName = Get(obj, "displayName") ?? Get(obj, "cn");
        if (string.IsNullOrWhiteSpace(displayName))
            return SinkWriteResult.Fail("Cannot upsert SCIM group without displayName.");

        var existingId = await TryFindGroupAsync(client, creds, obj.SourceId, displayName, ct);
        var members = new List<object>();
        if (obj.Attributes.TryGetValue("members", out var memVal) && memVal is System.Collections.IEnumerable list)
        {
            foreach (var m in list)
            {
                if (m is null) continue;
                var s = m as string ?? m.ToString();
                if (string.IsNullOrEmpty(s)) continue;
                members.Add(new { value = s, type = "User" });
            }
        }
        var payload = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            ["displayName"] = displayName,
            ["externalId"] = Get(obj, "externalId"),
            ["members"] = members
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/scim+json");

        if (existingId is null)
        {
            var resp = await client.PostAsync($"{creds.BaseUrl}{creds.GroupEndpoint}", content, ct);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"POST returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        var put = await client.PutAsync($"{creds.BaseUrl}{creds.GroupEndpoint}/{existingId}", content, ct);
        if (!put.IsSuccessStatusCode)
            return SinkWriteResult.Fail($"PUT returned HTTP {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(ct)}");
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static Dictionary<string, object?> BuildUserPayload(ConnectorObject obj, string userName)
    {
        var schemas = new List<string> { "urn:ietf:params:scim:schemas:core:2.0:User" };
        var name = new Dictionary<string, object?>();
        if (TryGet(obj, "givenName", out var gn)) name["givenName"] = gn;
        if (TryGet(obj, "familyName", out var fn)) name["familyName"] = fn;
        else if (TryGet(obj, "surname", out var sn)) name["familyName"] = sn;
        else if (TryGet(obj, "sn", out var sn2)) name["familyName"] = sn2;
        if (TryGet(obj, "displayName", out var dn)) name["formatted"] = dn;

        var emails = new List<object>();
        var primaryEmail = Get(obj, "email") ?? Get(obj, "mail");
        if (!string.IsNullOrEmpty(primaryEmail))
            emails.Add(new { value = primaryEmail, type = "work", primary = true });

        var payload = new Dictionary<string, object?>
        {
            ["schemas"] = schemas,
            ["userName"] = userName,
            ["externalId"] = Get(obj, "externalId"),
            ["displayName"] = Get(obj, "displayName"),
            ["title"] = Get(obj, "title") ?? Get(obj, "jobTitle"),
            ["userType"] = Get(obj, "userType"),
            ["active"] = TryGetBool(obj, "active", out var a) ? (object)a : (object?)null,
            ["name"] = name.Count > 0 ? name : null,
            ["emails"] = emails.Count > 0 ? emails : null
        };

        // Enterprise extension
        var ent = new Dictionary<string, object?>();
        if (TryGet(obj, "employeeNumber", out var eid)) ent["employeeNumber"] = eid;
        else if (TryGet(obj, "employeeId", out var eid2)) ent["employeeNumber"] = eid2;
        if (TryGet(obj, "department", out var dep)) ent["department"] = dep;
        if (TryGet(obj, "division", out var div)) ent["division"] = div;
        if (TryGet(obj, "costCenter", out var cc)) ent["costCenter"] = cc;
        if (TryGet(obj, "companyName", out var co)) ent["organization"] = co;
        else if (TryGet(obj, "company", out var co2)) ent["organization"] = co2;
        if (TryGet(obj, "manager", out var mgr)) ent["manager"] = new { value = mgr };
        if (ent.Count > 0)
        {
            schemas.Add("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User");
            payload["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"] = ent;
        }
        // Strip null leaves.
        foreach (var k in new List<string>(payload.Keys))
            if (payload[k] is null) payload.Remove(k);
        return payload;
    }

    private static async Task<string?> TryFindUserAsync(HttpClient client, ScimCredentials creds, string? id, string userName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var resp = await client.GetAsync($"{creds.BaseUrl}{creds.UserEndpoint}/{id}", ct);
            if (resp.IsSuccessStatusCode) return id;
        }
        var filter = Uri.EscapeDataString($"userName eq \"{userName}\"");
        var fr = await client.GetAsync($"{creds.BaseUrl}{creds.UserEndpoint}?filter={filter}&count=1", ct);
        if (!fr.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await fr.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("Resources", out var resEl)
            && resEl.ValueKind == JsonValueKind.Array
            && resEl.GetArrayLength() > 0)
        {
            return resEl[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        return null;
    }

    private static async Task<string?> TryFindGroupAsync(HttpClient client, ScimCredentials creds, string? id, string displayName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var resp = await client.GetAsync($"{creds.BaseUrl}{creds.GroupEndpoint}/{id}", ct);
            if (resp.IsSuccessStatusCode) return id;
        }
        var filter = Uri.EscapeDataString($"displayName eq \"{displayName}\"");
        var fr = await client.GetAsync($"{creds.BaseUrl}{creds.GroupEndpoint}?filter={filter}&count=1", ct);
        if (!fr.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await fr.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("Resources", out var resEl)
            && resEl.ValueKind == JsonValueKind.Array
            && resEl.GetArrayLength() > 0)
        {
            return resEl[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        return null;
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

    private static bool TryGetBool(ConnectorObject obj, string key, out bool value)
    {
        value = false;
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return false;
        if (v is bool b) { value = b; return true; }
        if (bool.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
        return false;
    }
}
