using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
///
/// JOB-SERVER PROVENANCE (Phase C): every bulk/membership/tombstone request carries
/// <c>SourceJobServerId</c> (this installation's durable <see cref="ConduitInstanceIdentity"/>
/// GUID) + <c>SourceJobServerName</c>. IC resolves the GUID to an Agents row
/// (auto-registering one if absent) and stamps <c>Objects.SourceJobServerId</c> with it.
///
/// PHASE D HOOK (rich write-back, NOT consumed yet): a write-back to a Conduit-synced
/// object will route via this provenance --
///   Objects.SourceJobServerId  -> Agents.Id
///   -> ApiKeys.AgentId (KeyType = 'Agent')   (the per-agent command-channel key)
///   -> AgentCommands.TargetAgentId           (dispatch the write to THAT job server)
/// and Objects.SourceConnectionId tells the agent which connection (domain) to write
/// through. The agent that synced the object is therefore the agent that owns the
/// write-back path back to the source directory. Stamping it here in Phase C is what
/// makes that Phase D dispatch possible.
/// </summary>
public sealed class IdentityCenterSink : IConnectorSink, ITombstoneEmittingSink, IGroupMembershipEmittingSink
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
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            // Sign-in EVENT batches go to a different endpoint entirely
            // (/api/objects/signin-logs/bulk) — they are append-only event records,
            // not directory objects, so they never hit the /api/objects/bulk upsert
            // path nor IC's Objects table. A signinlog Mapping step pumps these here
            // like any other class; we route by ObjectClass before the upsert chunker.
            if (IsSignInLogBatch(batch))
                return await PostSignInLogBatchAsync(client, creds, batch, cancellationToken);

            // M365 per-user usage rows go to the typed endpoint
            // (/api/objects/m365-usage/bulk) — they carry OneDrive/mailbox storage +
            // license + activity for the M365UsageReports table, NOT directory objects,
            // so they never hit the /api/objects/bulk upsert path.
            if (IsM365UsageBatch(batch))
                return await PostM365UsageBatchAsync(client, creds, batch, cancellationToken);

            // License-assignment rows go to the typed endpoint
            // (/api/objects/licenses/bulk) — they upsert LicensePools + LicenseAssignments,
            // NOT directory objects, so they never hit the /api/objects/bulk upsert path.
            if (IsLicenseBatch(batch))
                return await PostLicenseBatchAsync(client, creds, batch, cancellationToken);

            // App-role-assignment rows go to the typed endpoint
            // (/api/objects/app-role-assignments/bulk) — they record who has access to
            // which enterprise app, NOT directory objects.
            if (IsAppRoleBatch(batch))
                return await PostAppRoleBatchAsync(client, creds, batch, cancellationToken);

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
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");

            // Tombstone soft-delete is only implemented for the Objects table
            // (/api/objects/tombstones). When this sink targets the Identities table
            // there is no people-table tombstone endpoint, so emit nothing rather
            // than mis-route disappeared ids to the Objects endpoint (which would
            // resolve the wrong connection and could soft-delete unrelated accounts).
            // Safe no-op: the orchestrator simply doesn't prune, and Identities rows
            // are never destructively touched by a table-to-table sync.
            if (creds.Table == IcTable.Identities)
            {
                _logger.LogInformation(
                    "IC tombstone emission skipped: sink table=Identities has no tombstone endpoint (tenant={TenantId}, ids={Count}).",
                    _tenantId, distinct.Count);
                return TombstoneEmitResult.Nothing();
            }

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
                    Source = SanitizeSource(source),
                    SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                    SourceJobServerName = ConduitInstanceIdentity.Name,
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

    /// <summary>
    /// Group-membership second pass. POSTs group→member edges to IC's
    /// <c>POST /api/objects/group-memberships/bulk</c> AFTER the object upserts, so
    /// the groups and their members already exist on IC's side (IC silently skips
    /// any SourceUniqueId it cannot resolve under the connection). IC resolves
    /// <paramref name="source"/> → connectionId, then resolves the group + member
    /// SourceUniqueIds to Objects.Id under THAT connection — so <paramref name="source"/>
    /// MUST be the SAME value the upsert path stamps (the source-connection name).
    ///
    /// IC caps a single request at ~1000 member ids total (summed across the
    /// Memberships entries in one call). We chunk a group's members across multiple
    /// entries / calls so no single POST exceeds the cap. Best-effort: on a failed
    /// POST we log and continue with the next chunk rather than throwing.
    /// </summary>
    public async Task<int> EmitGroupMembershipsAsync(
        string source,
        IReadOnlyList<GroupMembership> memberships,
        CancellationToken cancellationToken)
    {
        if (memberships.Count == 0) return 0;

        IdentityCenterCredentials creds;
        HttpClient client;
        try
        {
            creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");

            // Group membership only exists on the Objects table. When this sink
            // targets the Identities (people) table there is no membership endpoint,
            // so emit nothing rather than mis-route edges.
            if (creds.Table == IcTable.Identities)
            {
                _logger.LogInformation(
                    "IC group-membership emission skipped: sink table=Identities has no membership endpoint (tenant={TenantId}, groups={Count}).",
                    _tenantId, memberships.Count);
                return 0;
            }

            client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC group-membership emission setup failed (tenant={TenantId})", _tenantId);
            return 0;
        }

        var sanitizedSource = SanitizeSource(source);
        // An edge = one member id under a group. IC caps a single POST at ~1000
        // member ids total; chunk so each call's summed member count stays under it.
        const int maxMemberIdsPerCall = 1000;

        int pushedGroups = 0, edgesPersistedTotal = 0, membersUnresolvedTotal = 0, groupsUnresolvedTotal = 0;
        var pending = new List<object>();
        var pendingMemberCount = 0;

        async Task FlushAsync()
        {
            if (pending.Count == 0) return;
            var body = new
            {
                BatchId = Guid.NewGuid(),
                Source = sanitizedSource,
                SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                SourceJobServerName = ConduitInstanceIdentity.Name,
                Memberships = pending
            };
            try
            {
                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/group-memberships/bulk", body, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "IC group-membership POST failed (tenant={TenantId}, entries={Entries}): HTTP {Status} {Detail}",
                        _tenantId, pending.Count, (int)resp.StatusCode, detail);
                }
                else
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    var root = doc.RootElement;
                    int edgesPersisted = root.TryGetProperty("edgesPersisted", out var eEl) && eEl.ValueKind == JsonValueKind.Number ? eEl.GetInt32() : pendingMemberCount;
                    int membersResolved = root.TryGetProperty("membersResolved", out var mrEl) && mrEl.ValueKind == JsonValueKind.Number ? mrEl.GetInt32() : pendingMemberCount;
                    int membersUnresolved = root.TryGetProperty("membersUnresolved", out var muEl) && muEl.ValueKind == JsonValueKind.Number ? muEl.GetInt32() : 0;
                    int groupsResolved = root.TryGetProperty("groupsResolved", out var grEl) && grEl.ValueKind == JsonValueKind.Number ? grEl.GetInt32() : pending.Count;
                    int groupsUnresolved = root.TryGetProperty("groupsUnresolved", out var guEl) && guEl.ValueKind == JsonValueKind.Number ? guEl.GetInt32() : 0;
                    edgesPersistedTotal += edgesPersisted;
                    membersUnresolvedTotal += membersUnresolved;
                    groupsUnresolvedTotal += groupsUnresolved;
                    _logger.LogInformation(
                        "IC group-membership chunk pushed (tenant={TenantId}, entries={Entries}, members={Members}, edgesPersisted={EdgesPersisted}, membersResolved={MembersResolved}, membersUnresolved={MembersUnresolved}, groupsResolved={GroupsResolved}, groupsUnresolved={GroupsUnresolved}).",
                        _tenantId, pending.Count, pendingMemberCount, edgesPersisted, membersResolved, membersUnresolved, groupsResolved, groupsUnresolved);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IC group-membership chunk threw (tenant={TenantId}, entries={Entries})", _tenantId, pending.Count);
            }
            pending.Clear();
            pendingMemberCount = 0;
        }

        foreach (var gm in memberships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(gm.GroupSourceId) || gm.MemberSourceIds.Count == 0)
                continue;

            pushedGroups++;

            // Chunk a single group's members across entries so one group with >cap
            // members spans multiple Memberships entries / POSTs.
            for (var offset = 0; offset < gm.MemberSourceIds.Count; offset += maxMemberIdsPerCall)
            {
                var count = Math.Min(maxMemberIdsPerCall, gm.MemberSourceIds.Count - offset);
                // If adding this slice would overflow the current call, flush first.
                if (pendingMemberCount > 0 && pendingMemberCount + count > maxMemberIdsPerCall)
                    await FlushAsync();

                var slice = new string[count];
                for (var i = 0; i < count; i++) slice[i] = gm.MemberSourceIds[offset + i];

                pending.Add(new { GroupSourceUniqueId = gm.GroupSourceId, MemberSourceUniqueIds = slice });
                pendingMemberCount += count;

                if (pendingMemberCount >= maxMemberIdsPerCall)
                    await FlushAsync();
            }
        }

        await FlushAsync();

        _logger.LogInformation(
            "IC group-membership emission complete (tenant={TenantId}, groups={Groups}, edgesPersisted={EdgesPersisted}, membersUnresolved={MembersUnresolved}, groupsUnresolved={GroupsUnresolved}).",
            _tenantId, pushedGroups, edgesPersistedTotal, membersUnresolvedTotal, groupsUnresolvedTotal);

        return edgesPersistedTotal;
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'identitycenter' credential stored." };
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);
            var probeUrl = creds.Table == IcTable.Identities
                ? $"{creds.BaseUrl}/api/identities/query?keyField=employeeId&page=1&pageSize=1"
                : $"{creds.BaseUrl}/api/objects/query?objectClass=user&page=1&pageSize=1";
            var resp = await client.GetAsync(probeUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return new ConnectorTestResult { IsSuccessful = false, Message = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}" };
            return new ConnectorTestResult { IsSuccessful = true, Message = $"Connected to {creds.BaseUrl} (table={creds.Table})." };
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
        return creds.Table == IcTable.Identities
            ? await PostIdentityBatchAsync(client, creds, slice, cancellationToken)
            : await PostObjectBatchAsync(client, creds, slice, cancellationToken);
    }

    private async Task<List<SinkWriteResult>> PostObjectBatchAsync(
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
            string? sourceConnection = null;
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
                // "_sourceConnection" is the SOURCE CONNECTION (domain) name, e.g.
                // "domain.local2", stamped by the orchestrator from
                // ctx.SourceTenant.Name. IC auto-seeds a DirectoryConnection named
                // exactly this value and groups the objects under that domain node
                // (matching IC's native domain nodes) instead of a literal "Conduit"
                // node. Like "_source", it is internal plumbing: lift it out so it is
                // NEVER written into IC's ObjectAttributes table.
                if (string.Equals(k, "_sourceConnection", StringComparison.OrdinalIgnoreCase))
                {
                    sourceConnection = v?.ToString();
                    continue;
                }
                attrs[k] = AttributeValueToString(v);
            }
            items.Add(new
            {
                SourceUniqueId = o.SourceId,
                ObjectClass = (o.ObjectClass ?? "User").ToLowerInvariant(),
                // Source = the source connection (domain) name so IC's DirectoryConnection
                // auto-seed names the connection after the domain, not "Conduit". Fall
                // back to "Conduit" for back-compat / safety if the stamp is missing.
                // NOTE: this MUST match the value the orchestrator passes to
                // EmitTombstonesAsync (ctx.SourceTenant.Name) so IC resolves the SAME
                // auto-seeded SourceConnectionId for upserts and tombstones.
                Source = SanitizeSource(sourceConnection),
                OriginalSource = originalSource ?? string.Empty,
                Attributes = attrs
            });
        }

        var body = new
        {
            BatchId = batchId,
            // This installation's durable job-server identity. IC resolves it to an
            // Agents row (auto-registering on first sight) and stamps
            // Objects.SourceJobServerId so every synced object carries the provenance
            // of the box that pushed it. One job server per push, so batch-level.
            SourceJobServerId = ConduitInstanceIdentity.InstanceId,
            SourceJobServerName = ConduitInstanceIdentity.Name,
            Items = items
        };

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

        return MapResults(slice, byId);
    }

    /// <summary>
    /// Identities-table sink. Maps each ConnectorObject to an IdentityBulkUpsertItem
    /// keyed on the deterministic employeeId (falling back to the object's SourceId,
    /// which the Identities source stamps as the keyValue). RAW field movement only —
    /// IC's /api/identities/bulk does NOT run PersonMatch/correlation.
    /// </summary>
    private async Task<List<SinkWriteResult>> PostIdentityBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> slice,
        CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid();

        // Map the inbound attribute bag to Identities column names. Source-side keys
        // vary (AD: givenName/sn/mail; IC Identities: FirstName/LastName/PrimaryEmail).
        // The IC endpoint allow-lists columns and ignores unknowns, so we forward a
        // superset and let IC filter — but we normalise the common AD/Entra-isms to
        // IC columns so a raw AD→Identities sync lands sensible data.
        static void Put(Dictionary<string, string?> d, string col, string? v)
        {
            if (!string.IsNullOrEmpty(v) && !d.ContainsKey(col)) d[col] = v;
        }

        var items = new List<object>(slice.Count);
        // Track the key we send per object so we can map IC's per-row result back.
        var keyOf = new List<string>(slice.Count);
        foreach (var o in slice)
        {
            string? Attr(string k) => LookupAttr(o, k);

            // The deterministic key: prefer an explicit employeeId, else the
            // SourceId (the Identities source sets this to the row's keyValue;
            // for AD it's the objectGUID — still a stable per-row key).
            var key = Attr("employeeId") ?? Attr("employeeID") ?? o.SourceId;

            var attrs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Put(attrs, "EmployeeId",        Attr("employeeId") ?? Attr("employeeID"));
            Put(attrs, "FirstName",         Attr("FirstName") ?? Attr("firstName") ?? Attr("givenName"));
            Put(attrs, "LastName",          Attr("LastName") ?? Attr("lastName") ?? Attr("sn"));
            Put(attrs, "DisplayName",       Attr("displayName"));
            Put(attrs, "PrimaryEmail",      Attr("primaryEmail") ?? Attr("email") ?? Attr("mail") ?? Attr("userPrincipalName"));
            Put(attrs, "UserPrincipalName", Attr("userPrincipalName") ?? Attr("upn"));
            Put(attrs, "Username",          Attr("userName") ?? Attr("sAMAccountName") ?? Attr("username"));
            Put(attrs, "Department",        Attr("department"));
            Put(attrs, "JobTitle",          Attr("jobTitle") ?? Attr("title"));
            Put(attrs, "MobilePhone",       Attr("mobilePhone") ?? Attr("mobile"));
            Put(attrs, "Status",            Attr("status"));
            // Pass through any attribute that is already a literal Identities column
            // name (the IC Identities source emits these), without clobbering the
            // normalised values above.
            foreach (var (k, v) in o.Attributes)
            {
                if (k.StartsWith("_", StringComparison.Ordinal)) continue;  // _source etc.
                Put(attrs, k, v?.ToString());
            }

            keyOf.Add(key);
            items.Add(new { KeyValue = key, Attributes = attrs });
        }

        var body = new { BatchId = batchId, KeyField = "employeeId", Items = items };

        using var resp = await client.PostAsJsonAsync($"{creds.BaseUrl}/api/identities/bulk", body, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
            var failures = new List<SinkWriteResult>(slice.Count);
            for (var i = 0; i < slice.Count; i++) failures.Add(SinkWriteResult.Fail(msg));
            return failures;
        }

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var byKey = new Dictionary<string, (string Outcome, string? Err)>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in resultsEl.EnumerateArray())
            {
                var kv = r.TryGetProperty("keyValue", out var sEl) ? sEl.GetString() : null;
                var outcome = r.TryGetProperty("outcome", out var oEl) ? oEl.GetString() : null;
                var err = r.TryGetProperty("errorMessage", out var eEl) ? eEl.GetString() : null;
                if (!string.IsNullOrEmpty(kv))
                    byKey[kv!] = (outcome ?? "Skipped", err);
            }
        }

        // Map results back by the key we sent, in slice order.
        var ordered = new List<SinkWriteResult>(slice.Count);
        for (var i = 0; i < slice.Count; i++)
        {
            if (!byKey.TryGetValue(keyOf[i], out var pair))
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

    private static bool IsSignInLogBatch(IReadOnlyList<ConnectorObject> batch)
    {
        // A Mapping step pumps a single object class, so the batch is homogeneous;
        // probe the first record's class.
        return batch.Count > 0
            && string.Equals(batch[0].ObjectClass, "signinlog", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sign-in EVENT emit. POSTs each ConnectorObject (built by the signinlog
    /// Mapping step from the camelCase Graph attribute set) to IC's
    /// <c>POST /api/objects/signin-logs/bulk</c>. Mirrors the upsert + membership
    /// paths: Source is the sanitized source-connection name (so IC resolves the
    /// SAME DirectoryConnection the user/group upserts landed under) and
    /// SourceJobServerId/Name carry this installation's provenance. Chunked to
    /// &lt;=1000 events per POST. Best-effort: a failed chunk is logged and the run
    /// proceeds (these are observability records, not the canonical directory).
    /// Returns one SinkWriteResult per input object in order so the orchestrator
    /// counts correctly.
    /// </summary>
    private async Task<List<SinkWriteResult>> PostSignInLogBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        // Source connection name is carried per-record on "_sourceConnection" (the
        // orchestrator stamps ctx.SourceTenant.Name); fall back to "Conduit".
        var source = SanitizeSource(LookupAttr(batch[0], "_sourceConnection"));
        const int maxEventsPerPost = 1000;
        var results = new List<SinkWriteResult>(batch.Count);

        for (var offset = 0; offset < batch.Count; offset += maxEventsPerPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(maxEventsPerPost, batch.Count - offset);

            var events = new List<object>(count);
            for (var i = 0; i < count; i++)
                events.Add(BuildSignInLogEvent(batch[offset + i]));

            var body = new
            {
                BatchId = Guid.NewGuid(),
                Source = source,
                SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                SourceJobServerName = ConduitInstanceIdentity.Name,
                Events = events
            };

            SinkWriteOutcome outcome;
            string? error = null;
            try
            {
                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/signin-logs/bulk", body, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    outcome = SinkWriteOutcome.Updated;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    var root = doc.RootElement;
                    int persisted = root.TryGetProperty("eventsPersisted", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : count;
                    int resolved = root.TryGetProperty("usersResolved", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    int unresolved = root.TryGetProperty("usersUnresolved", out var uEl) && uEl.ValueKind == JsonValueKind.Number ? uEl.GetInt32() : 0;
                    _logger.LogInformation(
                        "IC sign-in log chunk pushed (tenant={TenantId}, events={Events}, eventsPersisted={Persisted}, usersResolved={Resolved}, usersUnresolved={Unresolved}).",
                        _tenantId, count, persisted, resolved, unresolved);
                }
                else
                {
                    outcome = SinkWriteOutcome.Failed;
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    error = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                    _logger.LogError(
                        "IC sign-in log POST failed (tenant={TenantId}, events={Events}): HTTP {Status} {Detail}",
                        _tenantId, count, (int)resp.StatusCode, detail);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome = SinkWriteOutcome.Failed;
                error = ex.Message;
                _logger.LogError(ex, "IC sign-in log chunk threw (tenant={TenantId}, events={Events})", _tenantId, count);
            }

            for (var i = 0; i < count; i++)
                results.Add(outcome == SinkWriteOutcome.Failed
                    ? SinkWriteResult.Fail(error ?? "Failed")
                    : SinkWriteResult.Ok(outcome));
        }

        return results;
    }

    /// <summary>
    /// Map one signinlog ConnectorObject's camelCase attribute bag (set by
    /// <c>EntraSignInLogSource</c>) to an IC SignInLogEvent payload. Types are parsed
    /// back from their attribute-bag string form: signInDateTime → DateTime,
    /// errorCode → int?, isInteractive → bool.
    /// </summary>
    private static object BuildSignInLogEvent(ConnectorObject o)
    {
        return new
        {
            SignInId = LookupAttr(o, "signInId") ?? o.SourceId,
            UserSourceUniqueId = LookupAttr(o, "userSourceUniqueId") ?? string.Empty,
            UserPrincipalName = LookupAttr(o, "userPrincipalName"),
            SignInDateTime = ParseDateTime(LookupAttr(o, "signInDateTime")),
            AppDisplayName = LookupAttr(o, "appDisplayName"),
            AppId = LookupAttr(o, "appId"),
            ClientAppUsed = LookupAttr(o, "clientAppUsed"),
            DeviceDetail = LookupAttr(o, "deviceDetail"),
            IpAddress = LookupAttr(o, "ipAddress"),
            Location = LookupAttr(o, "location"),
            Status = LookupAttr(o, "status"),
            ErrorCode = ParseInt(LookupAttr(o, "errorCode")),
            RiskLevel = LookupAttr(o, "riskLevel"),
            RiskState = LookupAttr(o, "riskState"),
            ConditionalAccessStatus = LookupAttr(o, "conditionalAccessStatus"),
            IsInteractive = ParseBool(LookupAttr(o, "isInteractive")),
            ResourceDisplayName = LookupAttr(o, "resourceDisplayName"),
            ResourceId = LookupAttr(o, "resourceId")
        };
    }

    private static bool IsM365UsageBatch(IReadOnlyList<ConnectorObject> batch)
    {
        // A Mapping step pumps a single object class, so the batch is homogeneous;
        // probe the first record's class.
        return batch.Count > 0
            && string.Equals(batch[0].ObjectClass, "m365usage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M365 per-user usage emit. POSTs each ConnectorObject (built by
    /// <c>M365UsageReportSource.BuildUsageObject</c>) to IC's
    /// <c>POST /api/objects/m365-usage/bulk</c>. Mirrors the sign-in-log path: Source
    /// is the sanitized source-connection name (so IC resolves the SAME
    /// DirectoryConnection the user upserts landed under) and SourceJobServerId/Name
    /// carry this installation's provenance. Chunked to &lt;=1000 rows per POST.
    /// Best-effort: a failed chunk is logged and the run proceeds (these are
    /// observability/usage records, not the canonical directory). Returns one
    /// SinkWriteResult per input object in order so the orchestrator counts correctly.
    /// </summary>
    private async Task<List<SinkWriteResult>> PostM365UsageBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        var source = SanitizeSource(LookupAttr(batch[0], "_sourceConnection"));
        const int maxRowsPerPost = 1000;
        var results = new List<SinkWriteResult>(batch.Count);

        for (var offset = 0; offset < batch.Count; offset += maxRowsPerPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(maxRowsPerPost, batch.Count - offset);

            var rows = new List<object>(count);
            for (var i = 0; i < count; i++)
                rows.Add(BuildM365UsageRow(batch[offset + i]));

            var body = new
            {
                BatchId = Guid.NewGuid(),
                Source = source,
                SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                SourceJobServerName = ConduitInstanceIdentity.Name,
                Rows = rows
            };

            SinkWriteOutcome outcome;
            string? error = null;
            try
            {
                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/m365-usage/bulk", body, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    outcome = SinkWriteOutcome.Updated;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    var root = doc.RootElement;
                    int persisted = root.TryGetProperty("reportsPersisted", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : count;
                    int resolved = root.TryGetProperty("usersResolved", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    int unresolved = root.TryGetProperty("usersUnresolved", out var uEl) && uEl.ValueKind == JsonValueKind.Number ? uEl.GetInt32() : 0;
                    _logger.LogInformation(
                        "IC m365 usage chunk pushed (tenant={TenantId}, rows={Rows}, reportsPersisted={Persisted}, usersResolved={Resolved}, usersUnresolved={Unresolved}).",
                        _tenantId, count, persisted, resolved, unresolved);
                }
                else
                {
                    outcome = SinkWriteOutcome.Failed;
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    error = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                    _logger.LogError(
                        "IC m365 usage POST failed (tenant={TenantId}, rows={Rows}): HTTP {Status} {Detail}",
                        _tenantId, count, (int)resp.StatusCode, detail);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome = SinkWriteOutcome.Failed;
                error = ex.Message;
                _logger.LogError(ex, "IC m365 usage chunk threw (tenant={TenantId}, rows={Rows})", _tenantId, count);
            }

            for (var i = 0; i < count; i++)
                results.Add(outcome == SinkWriteOutcome.Failed
                    ? SinkWriteResult.Fail(error ?? "Failed")
                    : SinkWriteResult.Ok(outcome));
        }

        return results;
    }

    /// <summary>
    /// Map one m365usage ConnectorObject's attribute bag (set by
    /// <c>M365UsageReportSource.BuildUsageObject</c>) to an IC M365UsageRow payload.
    /// Attribute keys match the source's PascalCase emission exactly; LookupAttr is
    /// case-insensitive. Numeric/bool/date values are parsed back from string form.
    /// </summary>
    private static object BuildM365UsageRow(ConnectorObject o)
    {
        return new
        {
            UserPrincipalName = LookupAttr(o, "UserPrincipalName") ?? o.SourceId,
            DisplayName = LookupAttr(o, "DisplayName"),
            ReportRefreshDate = ParseNullableDateTime(LookupAttr(o, "ReportRefreshDate")),
            HasExchangeLicense = ParseBool(LookupAttr(o, "HasExchangeLicense")),
            HasOneDriveLicense = ParseBool(LookupAttr(o, "HasOneDriveLicense")),
            HasSharePointLicense = ParseBool(LookupAttr(o, "HasSharePointLicense")),
            HasTeamsLicense = ParseBool(LookupAttr(o, "HasTeamsLicense")),
            HasYammerLicense = ParseBool(LookupAttr(o, "HasYammerLicense")),
            ExchangeLastActivityDate = ParseNullableDateTime(LookupAttr(o, "ExchangeLastActivityDate")),
            OneDriveLastActivityDate = ParseNullableDateTime(LookupAttr(o, "OneDriveLastActivityDate")),
            SharePointLastActivityDate = ParseNullableDateTime(LookupAttr(o, "SharePointLastActivityDate")),
            TeamsLastActivityDate = ParseNullableDateTime(LookupAttr(o, "TeamsLastActivityDate")),
            YammerLastActivityDate = ParseNullableDateTime(LookupAttr(o, "YammerLastActivityDate")),
            OneDriveStorageUsedBytes = ParseLong(LookupAttr(o, "OneDriveStorageUsedBytes")),
            OneDriveStorageAllocatedBytes = ParseLong(LookupAttr(o, "OneDriveStorageAllocatedBytes")),
            MailboxStorageUsedBytes = ParseLong(LookupAttr(o, "MailboxStorageUsedBytes")),
            MailboxQuotaBytes = ParseLong(LookupAttr(o, "MailboxQuotaBytes")),
            OneDriveFilesViewed = ParseInt(LookupAttr(o, "OneDriveFilesViewed")),
            OneDriveFilesSynced = ParseInt(LookupAttr(o, "OneDriveFilesSynced")),
            TeamsChatMessages = ParseInt(LookupAttr(o, "TeamsChatMessages")),
            TeamsCallCount = ParseInt(LookupAttr(o, "TeamsCallCount")),
            TeamsMeetingCount = ParseInt(LookupAttr(o, "TeamsMeetingCount")),
            AssignedProducts = LookupAttr(o, "AssignedProducts")
        };
    }

    private static bool IsLicenseBatch(IReadOnlyList<ConnectorObject> batch)
    {
        // A Mapping step pumps a single object class, so the batch is homogeneous.
        return batch.Count > 0
            && string.Equals(batch[0].ObjectClass, "license", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// License-assignment emit. POSTs each ConnectorObject (built by
    /// <c>EntraLicenseSource.Build</c>) to IC's <c>POST /api/objects/licenses/bulk</c>.
    /// Mirrors the m365-usage path: Source is the sanitized source-connection name (so
    /// IC resolves the SAME DirectoryConnection the user upserts landed under) and
    /// SourceJobServerId/Name carry this installation's provenance. Chunked to &lt;=1000
    /// rows per POST. Best-effort: a failed chunk is logged and the run proceeds.
    /// Returns one SinkWriteResult per input object in order so the orchestrator counts
    /// correctly.
    /// </summary>
    private async Task<List<SinkWriteResult>> PostLicenseBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        var source = SanitizeSource(LookupAttr(batch[0], "_sourceConnection"));
        const int maxRowsPerPost = 1000;
        var results = new List<SinkWriteResult>(batch.Count);

        for (var offset = 0; offset < batch.Count; offset += maxRowsPerPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(maxRowsPerPost, batch.Count - offset);

            var rows = new List<object>(count);
            for (var i = 0; i < count; i++)
                rows.Add(BuildLicenseRow(batch[offset + i]));

            var body = new
            {
                BatchId = Guid.NewGuid(),
                Source = source,
                SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                SourceJobServerName = ConduitInstanceIdentity.Name,
                Rows = rows
            };

            SinkWriteOutcome outcome;
            string? error = null;
            try
            {
                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/licenses/bulk", body, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    outcome = SinkWriteOutcome.Updated;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    var root = doc.RootElement;
                    int pools = root.TryGetProperty("poolsUpserted", out var poEl) && poEl.ValueKind == JsonValueKind.Number ? poEl.GetInt32() : 0;
                    int persisted = root.TryGetProperty("assignmentsPersisted", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : count;
                    int resolved = root.TryGetProperty("usersResolved", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    int unresolved = root.TryGetProperty("usersUnresolved", out var uEl) && uEl.ValueKind == JsonValueKind.Number ? uEl.GetInt32() : 0;
                    _logger.LogInformation(
                        "IC license chunk pushed (tenant={TenantId}, rows={Rows}, pools={Pools}, assignmentsPersisted={Persisted}, usersResolved={Resolved}, usersUnresolved={Unresolved}).",
                        _tenantId, count, pools, persisted, resolved, unresolved);
                }
                else
                {
                    outcome = SinkWriteOutcome.Failed;
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    error = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                    _logger.LogError(
                        "IC license POST failed (tenant={TenantId}, rows={Rows}): HTTP {Status} {Detail}",
                        _tenantId, count, (int)resp.StatusCode, detail);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome = SinkWriteOutcome.Failed;
                error = ex.Message;
                _logger.LogError(ex, "IC license chunk threw (tenant={TenantId}, rows={Rows})", _tenantId, count);
            }

            for (var i = 0; i < count; i++)
                results.Add(outcome == SinkWriteOutcome.Failed
                    ? SinkWriteResult.Fail(error ?? "Failed")
                    : SinkWriteResult.Ok(outcome));
        }

        return results;
    }

    /// <summary>
    /// Map one "license" ConnectorObject's attribute bag (set by
    /// <c>EntraLicenseSource.Build</c>) to an IC LicenseAssignmentRow payload.
    /// Attribute keys match the source's PascalCase emission exactly; LookupAttr is
    /// case-insensitive. Numeric values are parsed back from string form.
    /// </summary>
    private static object BuildLicenseRow(ConnectorObject o)
    {
        return new
        {
            SkuId = LookupAttr(o, "SkuId"),
            SkuName = LookupAttr(o, "SkuName"),
            SkuPartNumber = LookupAttr(o, "SkuPartNumber"),
            TotalUnits = ParseInt(LookupAttr(o, "TotalUnits")),
            ConsumedUnits = ParseInt(LookupAttr(o, "ConsumedUnits")),
            WarningUnits = ParseInt(LookupAttr(o, "WarningUnits")),
            SuspendedUnits = ParseInt(LookupAttr(o, "SuspendedUnits")),
            UserPrincipalName = LookupAttr(o, "UserPrincipalName"),
            UserSourceUniqueId = LookupAttr(o, "UserSourceUniqueId"),
            AssignedAt = ParseNullableDateTime(LookupAttr(o, "AssignedAt")),
            AssignmentSource = LookupAttr(o, "AssignmentSource")
        };
    }

    private static bool IsAppRoleBatch(IReadOnlyList<ConnectorObject> batch)
    {
        return batch.Count > 0
            && string.Equals(batch[0].ObjectClass, "approleassignment", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// App-role-assignment emit. POSTs each ConnectorObject (built by
    /// <c>EntraAppRoleSource.Build</c>) to IC's
    /// <c>POST /api/objects/app-role-assignments/bulk</c>. Mirrors the m365-usage /
    /// license paths exactly (Source resolution, job-server provenance, &lt;=1000-row
    /// chunking, best-effort). Returns one SinkWriteResult per input object in order.
    /// </summary>
    private async Task<List<SinkWriteResult>> PostAppRoleBatchAsync(
        HttpClient client,
        IdentityCenterCredentials creds,
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        var source = SanitizeSource(LookupAttr(batch[0], "_sourceConnection"));
        const int maxRowsPerPost = 1000;
        var results = new List<SinkWriteResult>(batch.Count);

        for (var offset = 0; offset < batch.Count; offset += maxRowsPerPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(maxRowsPerPost, batch.Count - offset);

            var rows = new List<object>(count);
            for (var i = 0; i < count; i++)
                rows.Add(BuildAppRoleRow(batch[offset + i]));

            var body = new
            {
                BatchId = Guid.NewGuid(),
                Source = source,
                SourceJobServerId = ConduitInstanceIdentity.InstanceId,
                SourceJobServerName = ConduitInstanceIdentity.Name,
                Rows = rows
            };

            SinkWriteOutcome outcome;
            string? error = null;
            try
            {
                using var resp = await client.PostAsJsonAsync(
                    $"{creds.BaseUrl}/api/objects/app-role-assignments/bulk", body, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    outcome = SinkWriteOutcome.Updated;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                    var root = doc.RootElement;
                    int persisted = root.TryGetProperty("assignmentsPersisted", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : count;
                    int resolved = root.TryGetProperty("principalsResolved", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    int unresolved = root.TryGetProperty("principalsUnresolved", out var uEl) && uEl.ValueKind == JsonValueKind.Number ? uEl.GetInt32() : 0;
                    _logger.LogInformation(
                        "IC app-role chunk pushed (tenant={TenantId}, rows={Rows}, persisted={Persisted}, principalsResolved={Resolved}, principalsUnresolved={Unresolved}).",
                        _tenantId, count, persisted, resolved, unresolved);
                }
                else
                {
                    outcome = SinkWriteOutcome.Failed;
                    var detail = await resp.Content.ReadAsStringAsync(cancellationToken);
                    error = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                    _logger.LogError(
                        "IC app-role POST failed (tenant={TenantId}, rows={Rows}): HTTP {Status} {Detail}",
                        _tenantId, count, (int)resp.StatusCode, detail);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome = SinkWriteOutcome.Failed;
                error = ex.Message;
                _logger.LogError(ex, "IC app-role chunk threw (tenant={TenantId}, rows={Rows})", _tenantId, count);
            }

            for (var i = 0; i < count; i++)
                results.Add(outcome == SinkWriteOutcome.Failed
                    ? SinkWriteResult.Fail(error ?? "Failed")
                    : SinkWriteResult.Ok(outcome));
        }

        return results;
    }

    /// <summary>
    /// Map one "approleassignment" ConnectorObject's attribute bag (set by
    /// <c>EntraAppRoleSource.Build</c>) to an IC AppRoleAssignmentRow payload.
    /// </summary>
    private static object BuildAppRoleRow(ConnectorObject o)
    {
        return new
        {
            AppRoleAssignmentId = LookupAttr(o, "AppRoleAssignmentId") ?? o.SourceId,
            PrincipalId = LookupAttr(o, "PrincipalId"),
            PrincipalType = LookupAttr(o, "PrincipalType"),
            PrincipalDisplayName = LookupAttr(o, "PrincipalDisplayName"),
            ResourceId = LookupAttr(o, "ResourceId"),
            ResourceDisplayName = LookupAttr(o, "ResourceDisplayName"),
            AppRoleId = LookupAttr(o, "AppRoleId"),
            AppRoleName = LookupAttr(o, "AppRoleName"),
            CreatedDateTime = ParseNullableDateTime(LookupAttr(o, "CreatedDateTime"))
        };
    }

    private static DateTime ParseDateTime(string? v) =>
        DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : default;

    private static DateTime? ParseNullableDateTime(string? v) =>
        DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : (DateTime?)null;

    private static long? ParseLong(string? v) =>
        long.TryParse(v, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (long?)null;

    private static int? ParseInt(string? v) =>
        int.TryParse(v, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (int?)null;

    private static bool ParseBool(string? v) =>
        bool.TryParse(v, out var b) && b;

    private static List<SinkWriteResult> MapResults(
        IReadOnlyList<ConnectorObject> slice,
        Dictionary<string, (string Outcome, string? Err)> byId)
    {
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

    // ── Phase 5 provisioning: explicit create path ──────────────────────────
    //
    // CreateAsync is the INBOUND-PROXY front door (SCIM/REST POST → this sink).
    // Unlike UpsertBatchAsync (sync orchestrator path), it returns a rich
    // ProvisionResult carrying IC's assigned target id so the SCIM layer can
    // emit a real Location + id. It reuses the SAME single-item batch posters
    // (PostObjectBatchAsync / PostIdentityBatchAsync) the sync path uses — they
    // already honor the ambient IdentityCenterTableContext (V22 for sync; the
    // inbound proxy stamps it from the connection's TargetTable) and parse IC's
    // per-item outcome. After a Created/Updated outcome we resolve IC's actual
    // row id with the existing resolver helpers (one extra GET) so ExternalId is
    // IC's primary key, not the source id we sent — enabling GET/PUT/DELETE
    // round-trip by that id in Phase 2.
    public async Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            var single = new[] { newObject };
            var writeResults = creds.Table == IcTable.Identities
                ? await PostIdentityBatchAsync(client, creds, single, cancellationToken)
                : await PostObjectBatchAsync(client, creds, single, cancellationToken);

            if (writeResults.Count == 0)
                return ProvisionResult.Failed("IC sink returned no result for the create.");

            var r = writeResults[0];
            if (r.Outcome == SinkWriteOutcome.Failed)
                return ProvisionResult.Failed(r.ErrorMessage ?? "IC rejected the create.");

            // Resolve IC's assigned id so the caller gets IC's real primary key.
            // Best-effort: a successful write with an unresolved id still succeeds
            // (we fall back to the SourceUniqueId we sent). The inbound proxy
            // supplies SourceId = the SCIM externalId / generated key.
            string? externalId = null;
            try
            {
                if (creds.Table == IcTable.Identities)
                {
                    var idGuid = await ResolveIdentityIdAsync(client, creds, newObject.SourceId, cancellationToken);
                    externalId = idGuid?.ToString();
                }
                else
                {
                    var objClass = (newObject.ObjectClass ?? "user").ToLowerInvariant();
                    var idGuid = await ResolveObjectIdAsync(client, creds, objClass, newObject.SourceId, cancellationToken);
                    externalId = idGuid?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IC CreateAsync succeeded but id resolution failed (tenant={TenantId}, sourceId={Id}); returning source id.",
                    _tenantId, newObject.SourceId);
            }

            return ProvisionResult.Success(externalId: externalId ?? NullIfEmpty(newObject.SourceId));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC CreateAsync failed (tenant={TenantId})", _tenantId);
            return ProvisionResult.Failed(ex.Message);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── Phase 2 provisioning: explicit update path ──────────────────────────
    //
    // UpdateAsync is the INBOUND-PROXY update front door (SCIM PUT/PATCH or
    // /api/v1 PATCH → this sink). IC's /api/objects/bulk (and /api/identities/bulk)
    // is a MERGE upsert keyed on SourceUniqueId, so an update is the same POST as a
    // create with the externalId stamped as the SourceId — IC merges the supplied
    // attributes into the existing row.
    //
    // SEMANTICS: IC's bulk endpoint is natively PARTIAL (it merges the attributes
    // present in the payload; it does not delete columns absent from it). There is
    // no whole-object replace mode on the bulk endpoint, so we honor BOTH PUT
    // (<paramref name="replace"/>=true) and PATCH as a partial merge of the supplied
    // attributes. This is stated on the IC adapter's SupportsUpdate doc and surfaced
    // to the caller honestly (a PUT will not clear omitted attributes).
    //
    // The externalId arriving here is IC's row GUID (the id CreateAsync resolved and
    // the SCIM layer round-trips). IC's bulk keys on SourceUniqueId, NOT its GUID —
    // so when the caller's externalId is a GUID we first resolve it back to the row's
    // SourceUniqueId via the query endpoint, then merge by that. When externalId is
    // already a SourceUniqueId (e.g. a generic-REST caller that supplied externalId
    // on create), the resolve is a no-op miss and we merge by it directly.
    public async Task<ProvisionResult> UpdateAsync(string externalId, ConnectorObject changes, bool replace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return ProvisionResult.Failed("UpdateAsync requires a non-empty externalId.");

        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink)
                ?? throw new InvalidOperationException($"No 'identitycenter' credential for tenant {_tenantId}.");
            var client = IdentityCenterCredentialReader.BuildClient(_httpFactory, creds);

            // Resolve the externalId (IC's GUID) back to the row's SourceUniqueId so
            // the bulk MERGE addresses the right row. If it's not a resolvable GUID,
            // treat the externalId itself AS the SourceUniqueId (generic-REST case).
            var sourceUniqueId = await ResolveSourceUniqueIdAsync(client, creds, changes.ObjectClass, externalId, cancellationToken)
                                 ?? externalId;

            // Stamp the resolved key as the object's SourceId so PostObjectBatch /
            // PostIdentityBatch merge the existing row rather than minting a new one.
            var merged = new ConnectorObject
            {
                SourceId = sourceUniqueId,
                ObjectClass = changes.ObjectClass,
                Attributes = changes.Attributes
            };

            var single = new[] { merged };
            var writeResults = creds.Table == IcTable.Identities
                ? await PostIdentityBatchAsync(client, creds, single, cancellationToken)
                : await PostObjectBatchAsync(client, creds, single, cancellationToken);

            if (writeResults.Count == 0)
                return ProvisionResult.Failed("IC sink returned no result for the update.");

            var r = writeResults[0];
            if (r.Outcome == SinkWriteOutcome.Failed)
                return ProvisionResult.Failed(r.ErrorMessage ?? "IC rejected the update.");

            // Return IC's row GUID as the external id so the SCIM layer round-trips
            // the SAME id the caller addressed. Best-effort re-resolve; fall back to
            // the externalId we were handed.
            string? resolvedGuid = null;
            try
            {
                if (creds.Table == IcTable.Identities)
                    resolvedGuid = (await ResolveIdentityIdAsync(client, creds, sourceUniqueId, cancellationToken))?.ToString();
                else
                    resolvedGuid = (await ResolveObjectIdAsync(client, creds, (changes.ObjectClass ?? "user").ToLowerInvariant(), sourceUniqueId, cancellationToken))?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IC UpdateAsync succeeded but id re-resolution failed (tenant={TenantId}, key={Key}); returning caller id.",
                    _tenantId, sourceUniqueId);
            }

            return ProvisionResult.Success(externalId: resolvedGuid ?? NullIfEmpty(externalId));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC UpdateAsync failed (tenant={TenantId}, externalId={Id})", _tenantId, externalId);
            return ProvisionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Resolve an IC row GUID back to its SourceUniqueId via the query endpoint, so
    /// the bulk MERGE (which keys on SourceUniqueId) addresses the right row. Returns
    /// null when the input isn't a GUID that resolves to a live row — the caller then
    /// treats the input as the SourceUniqueId directly. Objects-table only; Identities
    /// merge by keyValue, which the caller already supplies as externalId.
    /// </summary>
    private static async Task<string?> ResolveSourceUniqueIdAsync(
        HttpClient client, IdentityCenterCredentials creds, string? objectClass, string externalId, CancellationToken ct)
    {
        if (creds.Table == IcTable.Identities) return null;          // identities key on keyValue, not GUID
        if (!Guid.TryParse(externalId, out _)) return null;          // not a GUID → already a SourceUniqueId
        var cls = (objectClass ?? "user").ToLowerInvariant();
        var url = $"{creds.BaseUrl}/api/objects/query?objectClass={Uri.EscapeDataString(cls)}&id={Uri.EscapeDataString(externalId)}&page=1&pageSize=1";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) return null;
        var e = itemsEl.EnumerateArray();
        if (!e.MoveNext()) return null;
        return e.Current.TryGetProperty("sourceUniqueId", out var sEl) ? sEl.GetString() : null;
    }

    /// <summary>
    /// Phase 2 deprovision. Soft-deletes the object identified by <paramref name="sourceId"/>
    /// on IC's side via the SAME tombstone endpoint the sync delete-detection uses
    /// (<see cref="EmitTombstonesAsync"/>), so deprovision is reversible and honors IC's
    /// 50% safety cap. The single id is resolved under the connection the upserts land
    /// under (the tombstone path resolves Source → connection exactly like the bulk path).
    ///
    /// Idempotent: an id IC can't match is treated as success (already gone). Identities
    /// has no tombstone endpoint, so a delete there returns Skipped rather than mis-routing.
    /// </summary>
    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return SinkWriteResult.Fail("DeleteAsync requires a non-empty sourceId.");

        try
        {
            var creds = await IdentityCenterCredentialReader.ReadAsync(_protector, _tenantId, CredentialSide.Sink);
            if (creds is null)
                return SinkWriteResult.Fail($"No 'identitycenter' credential for tenant {_tenantId}.");

            // Identities table has no people-table tombstone endpoint — mirror
            // EmitTombstonesAsync's guard rather than mis-route to the Objects endpoint.
            if (creds.Table == IcTable.Identities)
            {
                _logger.LogInformation(
                    "IC DeleteAsync skipped: sink table=Identities has no tombstone endpoint (tenant={TenantId}, id={Id}).",
                    _tenantId, sourceId);
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
            }

            // Source MUST match the value the upsert path stamps so IC resolves the
            // SAME connection. The inbound proxy doesn't carry a per-record _source,
            // so fall back to the canonical "Conduit" source the bulk path defaults to.
            var result = await EmitTombstonesAsync("Conduit", new[] { sourceId }, cancellationToken);
            if (!result.Succeeded)
                return SinkWriteResult.Fail(result.ErrorMessage ?? "IC tombstone delete failed.");
            if (result.Aborted)
                return SinkWriteResult.Fail(result.AbortReason ?? "IC 50% safety cap tripped; delete not applied.");

            // Matched==0 → the id wasn't a live IC row; idempotent success (already gone).
            return SinkWriteResult.Ok(
                result.SoftDeleted > 0 ? SinkWriteOutcome.Updated : SinkWriteOutcome.Skipped);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IC DeleteAsync failed (tenant={TenantId}, id={Id})", _tenantId, sourceId);
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

    /// <summary>
    /// Render an attribute value for IC's string-typed bulk payload. Multi-valued
    /// LDAP attributes (servicePrincipalName, memberOf, proxyAddresses) arrive from
    /// the AD source as List&lt;object?&gt; — a bare ToString() would emit
    /// "System.Collections.Generic.List`1[...]". IC's canonical multi-value format
    /// (its own AD + Entra connectors, AttributeMappingService.ConvertAttributeValueToString)
    /// is a semicolon-joined string, so match it.
    /// </summary>
    private static string? AttributeValueToString(object? v)
    {
        if (v is null) return null;
        if (v is string s) return s;
        if (v is System.Collections.IEnumerable e && v is not byte[])
        {
            var parts = new List<string>();
            foreach (var item in e)
            {
                var sv = AttributeValueToString(item);
                if (!string.IsNullOrEmpty(sv)) parts.Add(sv!);
            }
            return string.Join(";", parts);
        }
        return v.ToString();
    }

    /// <summary>
    /// Sanitize a source-connection name to the value IC stores as
    /// DirectoryConnections.Name (and validates per-record Source against). Delegates
    /// to <see cref="IdentityCenterSourceName.Sanitize"/> — the ONE shared sanitizer —
    /// so the Source this sink stamps is byte-identical to the credential-map key the
    /// orchestrator writes from the same ctx.SourceTenant.Name. Do NOT reimplement.
    /// </summary>
    private static string SanitizeSource(string? raw) => IdentityCenterSourceName.Sanitize(raw);

    private static string? LookupAttr(ConnectorObject obj, string key)
    {
        if (obj.Attributes.TryGetValue(key, out var v)) return v?.ToString();
        foreach (var (k, val) in obj.Attributes)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return val?.ToString();
        return null;
    }
}
