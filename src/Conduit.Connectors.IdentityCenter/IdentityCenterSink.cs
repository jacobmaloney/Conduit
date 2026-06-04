using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// IdentityCenter sink. Batches up to <see cref="IdentityCenterAdapter.Capabilities"/>'
/// MaxBatchSize (500) per <c>POST /api/objects/bulk</c> call. IC's controller
/// does NOT echo to AD / Entra — Conduit owns the directory write for the
/// canonical source, and IC absorbs the projection straight into its Objects
/// table. See <c>ObjectsController.BulkUpsert</c> for the reasoning.
/// </summary>
public sealed class IdentityCenterSink : IConnectorSink, ITombstoneEmittingSink
{
    private readonly Guid _tenantId;
    private readonly IHttpClientFactory _httpFactory;
    private readonly CredentialProtector _protector;
    private readonly ILogger<IdentityCenterSink> _logger;

    public IdentityCenterSink(Guid tenantId, IHttpClientFactory httpFactory, CredentialProtector protector, ILogger<IdentityCenterSink> logger)
    {
        _tenantId = tenantId;
        _httpFactory = httpFactory;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        var batchResults = await UpsertBatchAsync(new[] { obj }, cancellationToken);
        return batchResults.Count > 0 ? batchResults[0] : SinkWriteResult.Fail("Empty result");
    }

    public async Task<IReadOnlyList<SinkWriteResult>> UpsertBatchAsync(
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return Array.Empty<SinkWriteResult>();

        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            // IC accepts up to 1000 per call; adapter advertises 500 so the
            // orchestrator already pre-chunks. This is a defensive fallback.
            const int hardCap = 1000;
            var results = new List<SinkWriteResult>(batch.Count);
            for (var offset = 0; offset < batch.Count; offset += hardCap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = batch.Count - offset > hardCap
                    ? Slice(batch, offset, hardCap)
                    : Slice(batch, offset, batch.Count - offset);
                var sliceResults = await PostBatchAsync(client, creds, slice, cancellationToken);
                results.AddRange(sliceResults);
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IdentityCenter sink batch upsert failed (tenant={TenantId}, count={Count})", _tenantId, batch.Count);
            var failures = new List<SinkWriteResult>(batch.Count);
            for (var i = 0; i < batch.Count; i++) failures.Add(SinkWriteResult.Fail(ex.Message));
            return failures;
        }
    }

    /// <summary>
    /// Phase 2.2 tombstone emission. POSTs the SourceUniqueIds that Conduit
    /// detected as DISAPPEARED from a COMPLETE source read to IC's
    /// <c>POST /api/objects/tombstones</c> for soft-delete. The destructive action
    /// happens entirely on IC's side; Conduit only sends ids.
    ///
    /// <paramref name="source"/> MUST be the SAME value the upsert path stamps
    /// (<c>"Conduit"</c>) so IC resolves the SAME auto-seeded SourceConnectionId it
    /// upserts into — that resolution + IC's SourceConnectionId SQL guard is what
    /// gives per-connection scoping. IC enforces a 50% cap (returns Aborted=true
    /// rather than deleting) unless Override is set; Conduit does NOT set Override.
    ///
    /// IC caps a single request at 1000 ids; we chunk to stay under it. The caller
    /// (orchestrator) has already gated this on WasCompleteRead()==true.
    /// </summary>
    public async Task<TombstoneEmitResult> EmitTombstonesAsync(
        string source,
        IReadOnlyList<string> sourceUniqueIds,
        CancellationToken cancellationToken)
    {
        var distinct = new List<string>(sourceUniqueIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in sourceUniqueIds)
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                distinct.Add(id);

        if (distinct.Count == 0)
            return TombstoneEmitResult.Nothing();

        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            const int icHardCap = 1000;
            int totalRequested = 0, totalMatched = 0, totalSoftDeleted = 0;
            bool anyAborted = false;
            string? abortReason = null;
            // Ids IC actually soft-deleted, so the orchestrator can prune exactly
            // those from SinkRecordHashes. We can only know the COUNT per batch from
            // IC's response, not the per-id outcome — so we treat a non-aborted
            // batch as "these requested ids were processed" and let the orchestrator
            // prune the ids whose batch was not aborted. Aborted batches are NOT
            // pruned (the records still exist in IC, un-deleted).
            var confirmedDeletedIds = new List<string>(distinct.Count);

            for (var offset = 0; offset < distinct.Count; offset += icHardCap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(icHardCap, distinct.Count - offset);
                var slice = distinct.GetRange(offset, count);

                var body = new
                {
                    BatchId = Guid.NewGuid(),
                    Source = source,
                    SourceUniqueIds = slice,
                    Override = false   // never override the IC 50% cap from Conduit
                };

                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/tombstones", body, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "IC tombstone POST failed (tenant={TenantId}, slice={Count}): HTTP {Status} {Detail}",
                        _tenantId, slice.Count, (int)resp.StatusCode, detail);
                    return TombstoneEmitResult.Failed(
                        $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}", confirmedDeletedIds);
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                var root = doc.RootElement;
                var aborted = root.TryGetProperty("aborted", out var aEl) && aEl.ValueKind == JsonValueKind.True;
                int matched = root.TryGetProperty("matched", out var mEl) && mEl.ValueKind == JsonValueKind.Number ? mEl.GetInt32() : 0;
                int softDeleted = root.TryGetProperty("softDeleted", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt32() : 0;
                int requested = root.TryGetProperty("requested", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : slice.Count;

                totalRequested += requested;
                totalMatched += matched;
                totalSoftDeleted += softDeleted;

                if (aborted)
                {
                    anyAborted = true;
                    abortReason ??= root.TryGetProperty("abortReason", out var arEl) ? arEl.GetString() : "IC 50% cap tripped.";
                    _logger.LogWarning(
                        "IC tombstone batch ABORTED by IC 50% cap (tenant={TenantId}, slice={Count}): {Reason}",
                        _tenantId, slice.Count, abortReason);
                    // Do NOT prune these — IC did not delete them.
                }
                else
                {
                    // Non-aborted: IC processed this slice. Prune the slice ids the
                    // orchestrator sent so they aren't re-emitted next run. (Ids that
                    // didn't match a live IC row are harmless to prune from Conduit's
                    // hash cache — they're gone from the sink either way.)
                    confirmedDeletedIds.AddRange(slice);
                }
            }

            return new TombstoneEmitResult
            {
                Succeeded = true,
                Aborted = anyAborted,
                AbortReason = abortReason,
                Requested = totalRequested,
                Matched = totalMatched,
                SoftDeleted = totalSoftDeleted,
                PrunableIds = confirmedDeletedIds
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC tombstone emission failed (tenant={TenantId}, count={Count})", _tenantId, distinct.Count);
            return TombstoneEmitResult.Failed(ex.Message, Array.Empty<string>());
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'identitycenter' credential stored." };
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);
            var resp = await client.GetAsync($"{creds.BaseUrl}/api/objects/query?objectClass=user&page=1&pageSize=1", cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected to {creds.BaseUrl}." };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    private async Task<List<SinkWriteResult>> PostBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> slice,
        CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid();
        var items = new List<object>(slice.Count);
        foreach (var o in slice)
        {
            var attrs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? originalSource = null;
            foreach (var (k, v) in o.Attributes)
            {
                // SyncProjectOrchestrator stamps "_source" onto every inbound
                // ConnectorObject so the IC sink can preserve the upstream
                // origin (ActiveDirectory / EntraID / Okta / ...). Lift it out
                // of the attribute bag here — it doesn't belong in IC's
                // ObjectAttributes table; the typed OriginalSource column
                // is its destination.
                if (string.Equals(k, "_source", StringComparison.OrdinalIgnoreCase))
                {
                    originalSource = v?.ToString();
                    continue;
                }
                attrs[k] = v?.ToString();
            }
            items.Add(new
            {
                SourceUniqueId = o.SourceId,
                ObjectClass = (o.ObjectClass ?? "User").ToLowerInvariant(),
                Source = "Conduit",
                OriginalSource = originalSource ?? string.Empty,
                Attributes = attrs
            });
        }

        var body = new { BatchId = batchId, Items = items };

        using var resp = await client.PostAsJsonAsync($"{creds.BaseUrl}/api/objects/bulk", body, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
            var failures = new List<SinkWriteResult>(slice.Count);
            for (var i = 0; i < slice.Count; i++) failures.Add(SinkWriteResult.Fail(msg));
            return failures;
        }

        // Parse IC's per-item BulkUpsertResponse.Results back into SinkWriteResults
        // keyed on SourceUniqueId so the orchestrator counts correctly.
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var byId = new Dictionary<string, (string Outcome, string? Err)>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in resultsEl.EnumerateArray())
            {
                var srcId = r.TryGetProperty("sourceUniqueId", out var sEl) ? sEl.GetString() : null;
                var outcome = r.TryGetProperty("outcome", out var oEl) ? oEl.GetString() : null;
                var err = r.TryGetProperty("errorMessage", out var eEl) ? eEl.GetString() : null;
                if (!string.IsNullOrEmpty(srcId))
                    byId[srcId!] = (outcome ?? "Skipped", err);
            }
        }

        var ordered = new List<SinkWriteResult>(slice.Count);
        foreach (var o in slice)
        {
            if (!byId.TryGetValue(o.SourceId, out var pair))
            {
                ordered.Add(SinkWriteResult.Fail("Missing result row in IC response"));
                continue;
            }
            ordered.Add(pair.Outcome switch
            {
                "Created" => SinkWriteResult.Ok(SinkWriteOutcome.Created),
                "Updated" => SinkWriteResult.Ok(SinkWriteOutcome.Updated),
                "Skipped" => SinkWriteResult.Ok(SinkWriteOutcome.Skipped),
                _ => SinkWriteResult.Fail(pair.Err ?? "Failed")
            });
        }
        return ordered;
    }

    private static IReadOnlyList<ConnectorObject> Slice(IReadOnlyList<ConnectorObject> src, int offset, int count)
    {
        var list = new List<ConnectorObject>(count);
        for (var i = 0; i < count; i++) list.Add(src[offset + i]);
        return list;
    }

    // ── Phase 7 person-aware step methods ──────────────────────────────────
    //
    // Conduit's workflow router calls these against IC's Phase-7 endpoints
    // (see IdentitiesController + ObjectsController). All four reuse the same
    // X-API-Key + BaseUrl credential the bulk/query paths already use.

    public async Task<PersonMatchResult> MatchPersonAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null) return PersonMatchResult.Fail("No 'identitycenter' credential.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            var body = new
            {
                source = LookupAttr(obj, "_source"),
                sourceUniqueId = obj.SourceId,
                candidateKeys = new
                {
                    upn = LookupAttr(obj, "userPrincipalName") ?? LookupAttr(obj, "upn"),
                    email = LookupAttr(obj, "mail") ?? LookupAttr(obj, "email") ?? LookupAttr(obj, "PrimaryEmail"),
                    employeeId = LookupAttr(obj, "employeeID") ?? LookupAttr(obj, "employeeId"),
                    username = LookupAttr(obj, "sAMAccountName") ?? LookupAttr(obj, "userName") ?? LookupAttr(obj, "Username"),
                    firstName = LookupAttr(obj, "givenName") ?? LookupAttr(obj, "FirstName"),
                    lastName = LookupAttr(obj, "sn") ?? LookupAttr(obj, "LastName")
                }
            };

            using var resp = await client.PostAsJsonAsync($"{creds.BaseUrl}/api/identities/match", body, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return PersonMatchResult.Fail($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var matched = doc.RootElement.TryGetProperty("matched", out var mEl) && mEl.GetBoolean();
            if (!matched) return PersonMatchResult.Miss();

            var idStr = doc.RootElement.TryGetProperty("identityId", out var iEl) ? iEl.GetString() : null;
            var by = doc.RootElement.TryGetProperty("matchedBy", out var bEl) ? bEl.GetString() : null;
            double conf = doc.RootElement.TryGetProperty("confidence", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                ? cEl.GetDouble() : 0.0;
            return PersonMatchResult.Hit(idStr ?? string.Empty, conf, by ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC sink MatchPerson failed (tenant={TenantId}, sourceId={Id})", _tenantId, obj.SourceId);
            return PersonMatchResult.Fail(ex.Message);
        }
    }

    public async Task<PersonCreateResult> CreatePersonAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException("No 'identitycenter' credential.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            var firstName = LookupAttr(obj, "givenName") ?? LookupAttr(obj, "FirstName") ?? string.Empty;
            var lastName = LookupAttr(obj, "sn") ?? LookupAttr(obj, "LastName") ?? string.Empty;
            var email = LookupAttr(obj, "mail") ?? LookupAttr(obj, "email") ?? LookupAttr(obj, "userPrincipalName") ?? string.Empty;

            var body = new
            {
                firstName,
                lastName,
                email,
                department = LookupAttr(obj, "department") ?? LookupAttr(obj, "Department"),
                jobTitle = LookupAttr(obj, "title") ?? LookupAttr(obj, "JobTitle"),
                employeeId = LookupAttr(obj, "employeeID") ?? LookupAttr(obj, "employeeId")
            };

            using var resp = await client.PostAsJsonAsync($"{creds.BaseUrl}/api/identities", body, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                return PersonCreateResult.Fail($"HTTP {(int)resp.StatusCode}: {detail}");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var idStr = doc.RootElement.TryGetProperty("identityId", out var iEl) ? iEl.GetString() : null;
            if (string.IsNullOrEmpty(idStr)) return PersonCreateResult.Fail("Response missing identityId");
            return PersonCreateResult.Ok(idStr!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC sink CreatePerson failed (tenant={TenantId}, sourceId={Id})", _tenantId, obj.SourceId);
            return PersonCreateResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> AssignManagerAsync(string objectExternalId, string managerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException("No 'identitycenter' credential.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            // Conduit hands us the source-side id (AD objectGUID, etc.). IC resolves
            // to its Identity by a /match round-trip first so the PATCH target is the
            // right typed GUID, then patches manager.
            var targetId = await ResolveIdentityIdAsync(client, creds, objectExternalId, cancellationToken);
            if (targetId is null)
                return SinkWriteResult.Fail($"No IC Identity matches external id '{objectExternalId}'.");

            using var resp = await client.PatchAsJsonAsync(
                $"{creds.BaseUrl}/api/identities/{targetId}/manager",
                new { managerExternalId },
                cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC sink AssignManager failed (tenant={TenantId})", _tenantId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> AssignGroupOwnerAsync(string groupExternalId, string ownerExternalId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException("No 'identitycenter' credential.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            // Resolve the group's IC Objects row id via the query endpoint.
            var groupId = await ResolveObjectIdAsync(client, creds, "group", groupExternalId, cancellationToken);
            if (groupId is null)
                return SinkWriteResult.Fail($"No IC group Object matches external id '{groupExternalId}'.");

            using var resp = await client.PatchAsJsonAsync(
                $"{creds.BaseUrl}/api/objects/groups/{groupId}/owner",
                new { ownerExternalId },
                cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return SinkWriteResult.Fail($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC sink AssignGroupOwner failed (tenant={TenantId})", _tenantId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    // ── Resolver helpers ────────────────────────────────────────────────────

    private static async Task<Guid?> ResolveIdentityIdAsync(HttpClient client, IdentityCenterCredentials creds, string externalId, CancellationToken ct)
    {
        var body = new { sourceUniqueId = externalId, candidateKeys = new { upn = externalId, email = externalId } };
        using var resp = await client.PostAsJsonAsync($"{creds.BaseUrl}/api/identities/match", body, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("matched", out var mEl) || !mEl.GetBoolean()) return null;
        if (!doc.RootElement.TryGetProperty("identityId", out var iEl)) return null;
        return Guid.TryParse(iEl.GetString(), out var g) ? g : null;
    }

    private static async Task<Guid?> ResolveObjectIdAsync(HttpClient client, IdentityCenterCredentials creds, string objectClass, string externalId, CancellationToken ct)
    {
        // /api/objects/query supports objectClass + a free-form sourceUniqueId filter.
        var url = $"{creds.BaseUrl}/api/objects/query?objectClass={Uri.EscapeDataString(objectClass)}&sourceUniqueId={Uri.EscapeDataString(externalId)}&page=1&pageSize=1";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) return null;
        var enumerator = itemsEl.EnumerateArray();
        if (!enumerator.MoveNext()) return null;
        var first = enumerator.Current;
        if (!first.TryGetProperty("id", out var idEl)) return null;
        return Guid.TryParse(idEl.GetString(), out var g) ? g : null;
    }

    private static string? LookupAttr(ConnectorObject obj, string key)
    {
        if (obj.Attributes.TryGetValue(key, out var v)) return v?.ToString();
        foreach (var (k, val) in obj.Attributes)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return val?.ToString();
        return null;
    }
}
