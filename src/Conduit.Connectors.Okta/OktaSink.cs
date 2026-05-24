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

namespace Conduit.Connectors.Okta;

/// <summary>
/// Okta sink. Users:
///   - lookup by SourceId, fall back to login (search via ?q=login).
///   - create: POST /api/v1/users?activate=true with profile + credentials.password (random).
///   - update: POST /api/v1/users/{id} with { profile }.
/// Groups:
///   - lookup by SourceId, fall back to name (search via ?q=name).
///   - create: POST /api/v1/groups with { profile: { name, description } }.
///   - update: PUT /api/v1/groups/{id} with { profile }.
/// Delete:
///   - users: deactivate then DELETE (Okta required two-call delete).
///   - groups: DELETE /api/v1/groups/{id}.
/// </summary>
public sealed class OktaSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<OktaSink> _logger;

    public OktaSink(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<OktaSink> logger)
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
            var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'okta' credential for tenant {_tenantId}.");
            var client = OktaCredentialReader.BuildClient(_httpFactory, creds);
            return string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
                ? await UpsertGroupAsync(client, creds.OrgUrl, obj, cancellationToken)
                : await UpsertUserAsync(client, creds.OrgUrl, obj, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Okta sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'okta' credential for tenant {_tenantId}.");
            var client = OktaCredentialReader.BuildClient(_httpFactory, creds);
            // Try as user first: deactivate then delete (Okta requires two-step).
            var deact = await client.PostAsync($"{creds.OrgUrl}/api/v1/users/{sourceId}/lifecycle/deactivate?sendEmail=false", null, cancellationToken);
            if (deact.StatusCode != HttpStatusCode.NotFound)
            {
                var del = await client.DeleteAsync($"{creds.OrgUrl}/api/v1/users/{sourceId}", cancellationToken);
                if (del.IsSuccessStatusCode || del.StatusCode == HttpStatusCode.NotFound)
                    return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
                return SinkWriteResult.Fail($"User DELETE returned HTTP {(int)del.StatusCode}.");
            }
            // Fall through: try as group.
            var gdel = await client.DeleteAsync($"{creds.OrgUrl}/api/v1/groups/{sourceId}", cancellationToken);
            if (gdel.IsSuccessStatusCode || gdel.StatusCode == HttpStatusCode.NotFound)
                return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
            return SinkWriteResult.Fail($"Group DELETE returned HTTP {(int)gdel.StatusCode}.");
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
            var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return new ConnectorTestResult { IsSuccessful = false, Message = "No 'okta' credential stored." };
            var client = OktaCredentialReader.BuildClient(_httpFactory, creds);
            var resp = await client.GetAsync($"{creds.OrgUrl}/api/v1/users?limit=1", cancellationToken);
            return resp.IsSuccessStatusCode
                ? new ConnectorTestResult { IsSuccessful = true, Message = $"Okta sink ready at {creds.OrgUrl}." }
                : new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── Phase 8: AssignManager ─────────────────────────────────────────────
    //
    // Okta stores the manager-link as a free-form string field on the user
    // profile. The canonical key is "managerId" (matches IC's read pattern in
    // OktaQueryService — see GetStr(profile, "managerId")). We resolve both the
    // target user and the manager via TryFindUserIdAsync — accepts an Okta id or
    // a login/UPN — then PATCH profile.managerId. PATCH is via POST /Users/{id}
    // with a partial profile, matching how the Upsert path mutates profile fields.

    public async Task<SinkWriteResult> AssignManagerAsync(string userExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userExternalId))
                return SinkWriteResult.Fail("AssignManager: userExternalId is empty.");
            if (string.IsNullOrWhiteSpace(managerExternalId))
                return SinkWriteResult.Fail("AssignManager: managerExternalId is empty.");

            var creds = await OktaCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'okta' credential for tenant {_tenantId}.");
            var client = OktaCredentialReader.BuildClient(_httpFactory, creds);

            var userId = await TryFindUserIdAsync(client, creds.OrgUrl, userExternalId, userExternalId, cancellationToken);
            if (userId is null)
                return SinkWriteResult.Fail($"AssignManager: target user '{userExternalId}' not found in Okta.");

            // Resolve the manager too so we store the stable Okta id (matches IC's
            // managerId column expectation). If the manager isn't found we still
            // store the supplied value — Okta's profile.managerId is free-form.
            var managerId = await TryFindUserIdAsync(client, creds.OrgUrl, managerExternalId, managerExternalId, cancellationToken)
                            ?? managerExternalId;

            var body = JsonSerializer.Serialize(new
            {
                profile = new Dictionary<string, object?>
                {
                    ["managerId"] = managerId
                }
            });
            var resp = await client.PostAsync($"{creds.OrgUrl}/api/v1/users/{userId}",
                new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"AssignManager: PATCH returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(cancellationToken)}");

            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Okta AssignManager failed for {UserId}", userExternalId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    private async Task<SinkWriteResult> UpsertUserAsync(HttpClient client, string baseUrl, ConnectorObject obj, CancellationToken ct)
    {
        var login = Get(obj, "login") ?? Get(obj, "userPrincipalName") ?? Get(obj, "userName");
        if (string.IsNullOrWhiteSpace(login))
            return SinkWriteResult.Fail("Cannot upsert Okta user without login/userPrincipalName.");

        var existingId = await TryFindUserIdAsync(client, baseUrl, obj.SourceId, login, ct);

        var profile = new Dictionary<string, object?>
        {
            ["login"] = login,
            ["email"] = Get(obj, "email") ?? Get(obj, "mail") ?? login,
            ["firstName"] = Get(obj, "givenName"),
            ["lastName"] = Get(obj, "surname") ?? Get(obj, "sn") ?? Get(obj, "familyName"),
            ["displayName"] = Get(obj, "displayName"),
            ["department"] = Get(obj, "department"),
            ["title"] = Get(obj, "title") ?? Get(obj, "jobTitle"),
            ["organization"] = Get(obj, "company") ?? Get(obj, "companyName"),
            ["mobilePhone"] = Get(obj, "mobilePhone") ?? Get(obj, "mobile"),
            ["primaryPhone"] = Get(obj, "telephoneNumber"),
            ["streetAddress"] = Get(obj, "streetAddress"),
            ["city"] = Get(obj, "city") ?? Get(obj, "l"),
            ["state"] = Get(obj, "state") ?? Get(obj, "st"),
            ["zipCode"] = Get(obj, "postalCode"),
            ["countryCode"] = Get(obj, "country") ?? Get(obj, "co"),
            ["employeeNumber"] = Get(obj, "employeeId") ?? Get(obj, "employeeNumber"),
            ["division"] = Get(obj, "division")
        };
        TrimNulls(profile);

        if (existingId is null)
        {
            var body = JsonSerializer.Serialize(new
            {
                profile,
                credentials = new
                {
                    password = new { value = Guid.NewGuid().ToString("N") + "Aa1!" }
                }
            });
            var resp = await client.PostAsync($"{baseUrl}/api/v1/users?activate=true",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"User POST returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        var ubody = JsonSerializer.Serialize(new { profile });
        var uresp = await client.PostAsync($"{baseUrl}/api/v1/users/{existingId}",
            new StringContent(ubody, Encoding.UTF8, "application/json"), ct);
        if (!uresp.IsSuccessStatusCode)
            return SinkWriteResult.Fail($"User POST update returned HTTP {(int)uresp.StatusCode}: {await uresp.Content.ReadAsStringAsync(ct)}");
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private async Task<SinkWriteResult> UpsertGroupAsync(HttpClient client, string baseUrl, ConnectorObject obj, CancellationToken ct)
    {
        var name = Get(obj, "displayName") ?? Get(obj, "cn") ?? Get(obj, "name");
        if (string.IsNullOrWhiteSpace(name))
            return SinkWriteResult.Fail("Cannot upsert Okta group without name/displayName.");
        var description = Get(obj, "description");

        var existingId = await TryFindGroupIdAsync(client, baseUrl, obj.SourceId, name, ct);

        var profile = new { name, description };
        var body = JsonSerializer.Serialize(new { profile });
        if (existingId is null)
        {
            var resp = await client.PostAsync($"{baseUrl}/api/v1/groups",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"Group POST returned HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Created);
        }
        var put = await client.PutAsync($"{baseUrl}/api/v1/groups/{existingId}",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!put.IsSuccessStatusCode)
            return SinkWriteResult.Fail($"Group PUT returned HTTP {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(ct)}");
        return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
    }

    private static async Task<string?> TryFindUserIdAsync(HttpClient client, string baseUrl, string? id, string login, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var resp = await client.GetAsync($"{baseUrl}/api/v1/users/{id}", ct);
            if (resp.IsSuccessStatusCode) return id;
        }
        // Login lookup. Okta accepts login as URL segment too.
        var byLogin = await client.GetAsync($"{baseUrl}/api/v1/users/{Uri.EscapeDataString(login)}", ct);
        if (byLogin.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await byLogin.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        return null;
    }

    private static async Task<string?> TryFindGroupIdAsync(HttpClient client, string baseUrl, string? id, string name, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var resp = await client.GetAsync($"{baseUrl}/api/v1/groups/{id}", ct);
            if (resp.IsSuccessStatusCode) return id;
        }
        var search = await client.GetAsync($"{baseUrl}/api/v1/groups?q={Uri.EscapeDataString(name)}&limit=1", ct);
        if (!search.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await search.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        var first = doc.RootElement[0];
        return first.TryGetProperty("id", out var idEl2) ? idEl2.GetString() : null;
    }

    private static string? Get(ConnectorObject obj, string key) =>
        obj.Attributes.TryGetValue(key, out var v) && v is not null
            ? (v is string s ? (string.IsNullOrEmpty(s) ? null : s) : v.ToString())
            : null;

    private static void TrimNulls(Dictionary<string, object?> dict)
    {
        var keys = new List<string>(dict.Keys);
        foreach (var k in keys)
            if (dict[k] is null || (dict[k] is string s && string.IsNullOrEmpty(s)))
                dict.Remove(k);
    }
}
