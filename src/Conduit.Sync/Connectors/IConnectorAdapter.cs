using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;

namespace Conduit.Sync.Connectors;

/// <summary>
/// Every connector kind registers ONE adapter that exposes the source and/or
/// sink capabilities it supports. A pure-read connector (e.g. read-only LDAP)
/// returns null from CreateSink. A pure-write connector (e.g. emulator) returns
/// null from CreateSource. AD is both.
///
/// SystemType matches Tenants.SystemType strings ("ActiveDirectory", "Emulator",
/// "EntraID", ...). The ConnectorRegistry keys on this.
/// </summary>
public interface IConnectorAdapter
{
    /// <summary>Stable identifier — matches Tenants.SystemType.</summary>
    string SystemType { get; }

    /// <summary>Friendly label shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Returns true if a tenant of this type can be a sync source.</summary>
    bool SupportsSource { get; }

    /// <summary>Returns true if a tenant of this type can be a sync sink.</summary>
    bool SupportsSink { get; }

    /// <summary>
    /// Source + sink capability flags. Adapter declares once; orchestrator + UI
    /// branch on it. Defaults are conservative (single-record only).
    /// </summary>
    ConnectorCapabilities Capabilities => ConnectorCapabilities.Default;

    /// <summary>
    /// Credential types this adapter understands. Phase 2 multi-cred UX uses
    /// this to render a generic per-type field editor and to list a tenant's
    /// available credential names. An adapter with one type returns one entry.
    /// </summary>
    IReadOnlyList<CredentialTypeInfo> CredentialTypes => Array.Empty<CredentialTypeInfo>();

    /// <summary>
    /// Phase 6. Per-connector requirements on the Tenant entity itself (NOT the
    /// credential blob — those are <see cref="CredentialTypes"/>). Connection
    /// form reads this to render required asterisks + per-type help text +
    /// per-type placeholders, and to block Save when a required field is empty.
    /// Quick Sync also reads it to fail-fast before creating throw-away projects.
    /// AD declares Domain required (LDAP host). Most connectors return empty.
    /// </summary>
    IReadOnlyList<TenantFieldRequirement> TenantFieldRequirements =>
        Array.Empty<TenantFieldRequirement>();

    /// <summary>Returns null if this adapter doesn't support source operations.</summary>
    IConnectorSource? CreateSource(Guid tenantId);

    /// <summary>Returns null if this adapter doesn't support sink operations.</summary>
    IConnectorSink? CreateSink(Guid tenantId);

    /// <summary>
    /// Phase 4. Returns null when this adapter never submits async jobs (the
    /// default — most sinks complete synchronously). Adapters that DO submit
    /// async jobs (e.g. AWS SSO Admin assignments) return a per-tenant
    /// resolver the poller uses to advance pending rows.
    /// </summary>
    IConnectorAsyncJobResolver? CreateAsyncJobResolver(Guid tenantId) => null;

    /// <summary>
    /// Live directory-browse capability used by the wizard's Scope step to let
    /// the operator navigate and pick a Base DN instead of typing it. Mirrors
    /// IdentityCenter's OU/container browser. Returns null when the connector has
    /// no hierarchical container concept (Entra Graph, SCIM, CSV, Database, …) —
    /// the UI hides the Browse button in that case. LDAP-shaped adapters (AD,
    /// GenericLdap) return a per-tenant browser that binds with the tenant's
    /// stored credentials and enumerates one level of containers at a time.
    /// </summary>
    IConnectorContainerBrowser? CreateContainerBrowser(Guid tenantId) => null;
}

/// <summary>
/// Live container browser for hierarchical (LDAP-shaped) connectors. The wizard
/// Scope step calls this to enumerate one level of OUs/containers at a time so
/// the operator can drill down and select a Base DN without typing it.
///
/// Browsing is LIVE — it binds the connector's stored credentials against the
/// source directory on each call. Conduit holds no Objects lake (symmetric
/// router), so there is nothing to browse from cache; the bind is the only
/// source of truth. <paramref name="parentDn"/> null/empty means "start at the
/// directory root" (the source resolves its default naming context).
/// </summary>
public interface IConnectorContainerBrowser
{
    /// <summary>
    /// Enumerate the immediate child containers under <paramref name="parentDn"/>.
    /// Pass null/empty to list the root container(s). Implementations should
    /// surface a bind/search failure as a thrown exception OR by returning an
    /// empty list with <see cref="DirectoryBrowseResult.ErrorMessage"/> set —
    /// callers render the message rather than a bare tree.
    /// </summary>
    Task<DirectoryBrowseResult> BrowseContainersAsync(
        string? parentDn,
        CancellationToken cancellationToken);
}

/// <summary>
/// One container node returned by <see cref="IConnectorContainerBrowser"/>.
/// <see cref="HasChildren"/> is advisory — implementations may always set it
/// true and let the next drill-down reveal an empty level (matches IC, which
/// resolves children only on expand).
/// </summary>
public sealed class DirectoryContainerNode
{
    /// <summary>Short display name (ou / cn / name attribute).</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Full distinguished name — this is what gets written into Base DN.</summary>
    public string DistinguishedName { get; init; } = string.Empty;
    /// <summary>Optional description shown muted next to the name.</summary>
    public string? Description { get; init; }
    /// <summary>True if this node may contain further child containers (advisory).</summary>
    public bool HasChildren { get; init; } = true;
}

/// <summary>
/// Envelope from <see cref="IConnectorContainerBrowser.BrowseContainersAsync"/>.
/// Carries the resolved base (useful when the caller passed null and the source
/// substituted its defaultNamingContext) plus an optional error string so the
/// modal can show a clean message instead of swallowing failures.
/// </summary>
public sealed class DirectoryBrowseResult
{
    public IReadOnlyList<DirectoryContainerNode> Nodes { get; init; } = Array.Empty<DirectoryContainerNode>();
    /// <summary>The DN actually searched. Set on a root browse so the UI can show what auto-detect resolved to.</summary>
    public string? ResolvedBaseDn { get; init; }
    /// <summary>Non-null when the browse failed; the tree shows this instead of nodes.</summary>
    public string? ErrorMessage { get; init; }

    public static DirectoryBrowseResult Ok(IReadOnlyList<DirectoryContainerNode> nodes, string? resolvedBaseDn = null) =>
        new() { Nodes = nodes, ResolvedBaseDn = resolvedBaseDn };
    public static DirectoryBrowseResult Fail(string message) =>
        new() { ErrorMessage = message };
}

/// <summary>
/// Source-side capability: paged enumeration of objects matching a scope.
/// </summary>
public interface IConnectorSource
{
    /// <summary>
    /// Enumerate objects of the given class, honoring the supplied scope (BaseDN /
    /// LdapFilter / QueryExpression / PageSize / MaxObjects). Streams via async
    /// enumerable so the orchestrator can pump to the sink without loading the
    /// whole result set into memory.
    /// </summary>
    IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Phase 2 incremental path. Implementations that don't support cursors fall
    /// back to <see cref="ReadAsync"/> via the default and return a null cursor
    /// so the orchestrator stops persisting it. When <paramref name="cursor"/>
    /// is null or invalid, behave as a full read.
    /// </summary>
    Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
        => Task.FromResult(new SyncEnumerationResult
        {
            Objects = ReadAsync(objectClass, scope, cancellationToken),
            ResolveNewCursor = () => null,
            IsIncremental = false
        });

    /// <summary>Health probe. Returns IsSuccessful + a short message.</summary>
    Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sink-side capability: upsert + delete by source id.
/// Phase 2 adds <see cref="UpsertBatchAsync"/>. The default implementation loops
/// over single-record <see cref="UpsertAsync"/> so existing sinks keep working
/// unchanged. Sinks that advertise <c>SupportsBulk = true</c> in their adapter's
/// <see cref="ConnectorCapabilities"/> should override the batch path.
/// </summary>
public interface IConnectorSink
{
    /// <summary>
    /// Insert or update the object in the sink. Implementations decide create-vs-
    /// update via their own lookup keyed on ConnectorObject.SourceId. Returns
    /// what happened so the orchestrator can update counters.
    /// </summary>
    Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken);

    /// <summary>
    /// Batched upsert. Default fans out to <see cref="UpsertAsync"/> one at a
    /// time and reports each outcome in order. Sinks with a native bulk API
    /// (SCIM /Bulk, Graph $batch, AWS BulkOperations, batched SQL MERGE) should
    /// override this and report per-record outcomes.
    /// </summary>
    async Task<IReadOnlyList<SinkWriteResult>> UpsertBatchAsync(
        IReadOnlyList<ConnectorObject> batch,
        CancellationToken cancellationToken)
    {
        var results = new List<SinkWriteResult>(batch.Count);
        foreach (var obj in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await UpsertAsync(obj, cancellationToken));
            }
            catch (Exception ex)
            {
                results.Add(SinkWriteResult.Fail(ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Delete the object identified by sourceId. Implementations should treat a
    /// missing record as success (idempotent). Returning Skipped means the sink
    /// chose not to act (e.g. soft-delete unsupported).
    /// </summary>
    Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken) =>
        Task.FromResult(SinkWriteResult.Ok(SinkWriteOutcome.Skipped));

    /// <summary>Health probe. Returns IsSuccessful + a short message.</summary>
    Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken);

    // ─── Phase 7: person-aware step methods ──────────────────────────────────
    //
    // The orchestrator's WorkflowStep router calls these when a Workflow step's
    // StepType is PersonMatch / PersonCreate / AssignManager / AssignGroupOwner.
    // Default implementations throw NotSupportedException so existing sinks keep
    // compiling unchanged — adapters that DO implement these flip the matching
    // capability flag (SupportsPersonMatch / SupportsPersonCreate / etc.) so the
    // UI can grey out unavailable step types per sink.
    //
    // Conduit calls; the sink decides how the operation actually lands. IC sink
    // POSTs to IC's /api/identities endpoints. AD sink writes the `manager` /
    // `managedBy` LDAP attribute. EntraID sink calls Graph Manager/Owner refs.

    /// <summary>
    /// Look up an existing Person record that matches the given object.
    /// Implementations match on whatever identifiers fit the sink's data model
    /// (UPN, email, employeeId). Returns <c>MatchedIdentityId=null</c> on miss.
    /// </summary>
    Task<PersonMatchResult> MatchPersonAsync(ConnectorObject obj, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement MatchPersonAsync.");

    /// <summary>
    /// Create a new Person record from the given object's attributes. Used after
    /// a <see cref="MatchPersonAsync"/> miss. The PersonCreate step typically
    /// follows a PersonMatch step in the workflow tree.
    /// </summary>
    Task<PersonCreateResult> CreatePersonAsync(ConnectorObject obj, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement CreatePersonAsync.");

    /// <summary>
    /// Set the manager link on the target object. Both ids are sink-side external
    /// identifiers — AD uses DN, Entra/IC use object/identity GUIDs.
    /// </summary>
    Task<SinkWriteResult> AssignManagerAsync(string objectExternalId, string managerExternalId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement AssignManagerAsync.");

    /// <summary>
    /// Set the owner link on a Group. <paramref name="groupExternalId"/> is the
    /// sink-side group identifier; <paramref name="ownerExternalId"/> is the
    /// sink-side identifier of the user that should own the group.
    /// </summary>
    Task<SinkWriteResult> AssignGroupOwnerAsync(string groupExternalId, string ownerExternalId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement AssignGroupOwnerAsync.");

    // ─── Phase 5: provisioning step methods ─────────────────────────────────
    //
    // The /api/v1/provision, /api/v1/users/{id}/move, and /api/v1/users/{id}/reset-password
    // endpoints route here. CenturyCity's Worker (and any other governance-layer
    // caller) hits these via the HTTP surface. Default implementations throw
    // NotSupportedException so existing sinks that don't provision keep compiling
    // unchanged; the controller layer translates the exception into a clean
    // ProvisioningOutcome.NotSupported response so the caller can mark the
    // workflow step Failed instead of silently completed.
    //
    // Per-sink capability flags (SupportsCreate / SupportsMove / SupportsResetPassword)
    // let the controller short-circuit before instantiating connector machinery.

    /// <summary>
    /// Create a new object in the target system. The <paramref name="newObject"/>
    /// carries the desired attributes; SourceId may be empty (new account). Sinks
    /// that support creation return the assigned external id (DN / Graph id / etc.)
    /// in <see cref="ProvisionResult.ExternalId"/>. Sinks that do not should leave
    /// the default <see cref="NotSupportedException"/> in place.
    /// </summary>
    Task<ProvisionResult> CreateAsync(ConnectorObject newObject, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement CreateAsync.");

    /// <summary>
    /// Phase 2 inbound-proxy UPDATE front door (SCIM PUT/PATCH or /api/v1 PATCH →
    /// this sink). Mirrors <see cref="CreateAsync"/>: a rich <see cref="ProvisionResult"/>
    /// so the controller can map to a clean SCIM 200 / 501 / 502.
    ///
    /// <paramref name="externalId"/> is the sink-side identity of the row to update
    /// (the SCIM resource id, which for IC IS the target row GUID; for AD a DN/GUID;
    /// for Entra the object GUID). <paramref name="changes"/> carries the desired
    /// attribute set; its SourceId is set to <paramref name="externalId"/> by the
    /// proxy so a sink that keys upsert on SourceId addresses the right row.
    ///
    /// <paramref name="replace"/> = true for PUT (full replace where the sink can
    /// express it), false for PATCH (partial merge). Most directory back-ends
    /// (Graph PATCH, AD ModifyRequest with only the supplied attrs, IC bulk merge)
    /// are natively PARTIAL — a sink that cannot express a true full replace should
    /// document that PUT is honored as a partial merge of the supplied attributes
    /// rather than silently clearing omitted ones.
    ///
    /// DEFAULT delegates to <see cref="UpsertAsync"/> (which every sink implements
    /// as find-then-update-or-create) and maps the outcome. A sink whose adapter
    /// advertises <see cref="ConnectorCapabilities.SupportsUpdate"/> may override
    /// for richer semantics. A sink that advertises SupportsUpdate=false leaves
    /// this default; the proxy short-circuits to 501 before ever calling it, so
    /// the default is never reached for a non-update connection.
    /// </summary>
    Task<ProvisionResult> UpdateAsync(string externalId, ConnectorObject changes, bool replace, CancellationToken cancellationToken) =>
        UpdateViaUpsertAsync(externalId, changes, cancellationToken);

    /// <summary>
    /// Default UPDATE implementation shared by sinks that have no dedicated update
    /// API beyond their upsert: stamp the target id onto the object's SourceId and
    /// route through <see cref="UpsertAsync"/>, then translate the SinkWriteResult
    /// into a ProvisionResult. Created (the row didn't exist) and Updated both
    /// surface as Success — an idempotent update that had to insert is still a
    /// successful update from the caller's view; Skipped → Success (no-op), Failed
    /// → Failed. Sealed as a sink helper so overrides can also call it.
    /// </summary>
    protected async Task<ProvisionResult> UpdateViaUpsertAsync(string externalId, ConnectorObject changes, CancellationToken cancellationToken)
    {
        var target = string.IsNullOrEmpty(externalId)
            ? changes
            : new ConnectorObject { SourceId = externalId, ObjectClass = changes.ObjectClass, Attributes = changes.Attributes };
        var r = await UpsertAsync(target, cancellationToken);
        return r.Outcome switch
        {
            SinkWriteOutcome.Created => ProvisionResult.Success(externalId),
            SinkWriteOutcome.Updated => ProvisionResult.Success(externalId),
            SinkWriteOutcome.Skipped => ProvisionResult.Success(externalId),
            _ => ProvisionResult.Failed(r.ErrorMessage ?? "Sink rejected the update."),
        };
    }

    /// <summary>
    /// Move an existing object to a new container. AD-shaped in practice but the
    /// contract is generic — Entra-style sinks that have no container concept
    /// throw NotSupported. <paramref name="externalId"/> may be a DN, GUID, UPN
    /// or whatever the sink natively resolves; the sink does its own lookup.
    /// </summary>
    Task<ProvisionResult> MoveAsync(string externalId, string newContainer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement MoveAsync.");

    /// <summary>
    /// Reset a user's password. Sinks that need a secure channel (e.g. AD's
    /// unicodePwd over LDAPS) should fail-fast with a clear error when the
    /// channel isn't secure. <paramref name="requireChangeAtNextLogin"/> hints
    /// to set the force-change flag where the target supports it.
    /// </summary>
    Task<ProvisionResult> ResetPasswordAsync(string externalId, string newPassword, bool requireChangeAtNextLogin, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This sink does not implement ResetPasswordAsync.");
}

/// <summary>
/// Phase 2.2 tombstone capability. A sink that can soft-delete records the source
/// dropped implements this IN ADDITION to <see cref="IConnectorSink"/>. The
/// orchestrator checks <c>sink is ITombstoneEmittingSink</c> and only then — and
/// only on a proven complete source read — emits the disappeared ids. Keeping this
/// a separate interface (not a method on IConnectorSink) means only sinks that
/// genuinely support reversible soft-delete opt in; every other sink is inert by
/// construction, which is the safe default for a destructive operation.
///
/// Lives in Conduit.Sync so the orchestrator can reference it without inverting the
/// connector→Sync dependency direction; the IdentityCenter connector implements it.
/// </summary>
public interface ITombstoneEmittingSink
{
    /// <summary>
    /// Emit the disappeared <paramref name="sourceUniqueIds"/> for soft-delete on
    /// the sink side. <paramref name="source"/> MUST be the same connection
    /// identifier the sink stamps on its upserts so the sink resolves the SAME
    /// target connection (per-connection scoping). Implementations NEVER hard-delete
    /// and NEVER override the sink's own safety cap. Returns counts + the ids that
    /// were durably actioned (safe for the orchestrator to prune from its cache).
    /// </summary>
    Task<TombstoneEmitResult> EmitTombstonesAsync(
        string source,
        IReadOnlyList<string> sourceUniqueIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// One group→members edge to push to the sink. <see cref="GroupSourceId"/> is the
/// group's source-side stable id (the SAME value used as the group object's
/// SourceUniqueId on the upsert path); <see cref="MemberSourceIds"/> are the
/// members' source-side ids (cloud: member GUIDs/ids; AD: member DNs pending
/// objectGUID reconciliation). The sink resolves both to its own object ids under
/// the same connection the upserts landed under.
/// </summary>
public sealed record GroupMembership(string GroupSourceId, IReadOnlyList<string> MemberSourceIds);

/// <summary>
/// Phase-membership capability. A sink that can absorb group→member edges as a
/// SECOND PASS (after the object upserts, so groups and members already exist on
/// the sink side) implements this IN ADDITION to <see cref="IConnectorSink"/>. The
/// orchestrator probes <c>sink is IGroupMembershipEmittingSink</c> and only then —
/// and only on a proven complete read — pushes the captured membership buffer.
/// Mirrors <see cref="ITombstoneEmittingSink"/>: a separate opt-in interface so
/// sinks that have no membership endpoint stay inert by construction.
///
/// Lives in Conduit.Sync so the orchestrator can reference it without inverting the
/// connector→Sync dependency direction; the IdentityCenter connector implements it.
/// </summary>
public interface IGroupMembershipEmittingSink
{
    /// <summary>
    /// Push the captured <paramref name="memberships"/> to the sink. <paramref name="source"/>
    /// MUST be the same connection identifier the sink stamps on its upserts so the
    /// sink resolves the SAME target connection (per-connection scoping). Best-effort:
    /// implementations log and continue on failure rather than throwing. Returns the
    /// count of edges (group+member pairs) the sink accepted.
    /// </summary>
    Task<int> EmitGroupMembershipsAsync(
        string source,
        IReadOnlyList<GroupMembership> memberships,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="ITombstoneEmittingSink.EmitTombstonesAsync"/>. Carries the
/// sink-reported counts plus the ids the orchestrator may safely prune from its
/// SinkRecordHashes cache (durably-actioned ids only — capped/aborted ids were NOT
/// deleted and must remain so they're re-evaluated next run).
/// </summary>
public sealed class TombstoneEmitResult
{
    public bool Succeeded { get; init; }
    /// <summary>True if ANY batch was aborted by the sink's safety cap.</summary>
    public bool Aborted { get; init; }
    public string? AbortReason { get; init; }
    public int Requested { get; init; }
    public int Matched { get; init; }
    public int SoftDeleted { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Ids whose tombstone batch the sink accepted (not aborted) — safe to prune.</summary>
    public IReadOnlyList<string> PrunableIds { get; init; } = Array.Empty<string>();

    public static TombstoneEmitResult Nothing() =>
        new() { Succeeded = true, PrunableIds = Array.Empty<string>() };

    public static TombstoneEmitResult Failed(string message, IReadOnlyList<string> prunable) =>
        new() { Succeeded = false, ErrorMessage = message, PrunableIds = prunable };
}

/// <summary>
/// Declared once per adapter. The orchestrator reads <see cref="MaxBatchSize"/>
/// and <see cref="SupportsBulk"/> to decide whether to accumulate or stream
/// single-record. UI reads <see cref="SupportsIncremental"/> to label projects.
/// </summary>
public sealed class ConnectorCapabilities
{
    /// <summary>Sink can accept multi-record upserts (overrides UpsertBatchAsync).</summary>
    public bool SupportsBulk { get; init; }

    /// <summary>
    /// Maximum batch size the sink accepts in one UpsertBatchAsync call. Hard
    /// upper bound — SCIM /Bulk allows ~1000, Graph $batch is 20, AWS depends.
    /// Ignored when SupportsBulk is false.
    /// </summary>
    public int MaxBatchSize { get; init; } = 1;

    /// <summary>Source can resume from a SyncCursor (delta / lastModified).</summary>
    public bool SupportsIncremental { get; init; }

    /// <summary>
    /// SOURCE-side safety declaration: a record missing from this source's read
    /// is NOT evidence of deletion, so the orchestrator must never compute or
    /// forward tombstones for runs whose source declares this — regardless of
    /// the sink's tombstone capability. Set by discovery-style sources (e.g.
    /// SQL Discovery, where a failed scan is indistinguishable from a
    /// decommission). Default false (normal directory sources).
    /// </summary>
    public bool SuppressDeleteDetection { get; init; }

    /// <summary>
    /// SOURCE-side declaration: attribute names whose values change on every read
    /// without representing a real object change (per-attempt timestamps and the
    /// like). The orchestrator EXCLUDES these from the sink-side skip-unchanged
    /// content hash so an otherwise-unchanged record is skipped, while every other
    /// attribute still ingests normally (the excluded attributes themselves are
    /// still written when the record DOES change). Default empty — hashing
    /// semantics for every existing connector are untouched. Matched
    /// case-insensitively.
    /// </summary>
    public IReadOnlyList<string> HashVolatileAttributes { get; init; } = Array.Empty<string>();

    // ─── Phase 7 person-aware step support ──────────────────────────────────
    /// <summary>Sink implements <see cref="IConnectorSink.MatchPersonAsync"/>.</summary>
    public bool SupportsPersonMatch { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.CreatePersonAsync"/>.</summary>
    public bool SupportsPersonCreate { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.AssignManagerAsync"/>.</summary>
    public bool SupportsAssignManager { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.AssignGroupOwnerAsync"/>.</summary>
    public bool SupportsAssignGroupOwner { get; init; }

    // ─── Phase 5 provisioning support ───────────────────────────────────────
    /// <summary>Sink implements <see cref="IConnectorSink.CreateAsync"/>.</summary>
    public bool SupportsCreate { get; init; }
    /// <summary>
    /// Sink can UPDATE an existing object by external id via the inbound proxy
    /// (<see cref="IConnectorSink.UpdateAsync"/>). The proxy short-circuits PUT/PATCH
    /// to 501 when this is false, so it never reaches the default upsert delegate for
    /// a connection that has no genuine update primitive. Today: IdentityCenter (bulk
    /// merge), ActiveDirectory (ModifyRequest), EntraID (Graph PATCH).
    /// </summary>
    public bool SupportsUpdate { get; init; }
    /// <summary>
    /// Sink genuinely implements <see cref="IConnectorSink.DeleteAsync"/> (vs the
    /// interface default that returns Skipped). The inbound proxy gates DELETE on
    /// this so a writable target that can't deprovision returns 501 rather than a
    /// silent no-op. Today: IdentityCenter (reversible tombstone soft-delete),
    /// ActiveDirectory (DeleteRequest), EntraID (Graph DELETE).
    /// </summary>
    public bool SupportsDelete { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.MoveAsync"/>.</summary>
    public bool SupportsMove { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.ResetPasswordAsync"/>.</summary>
    public bool SupportsResetPassword { get; init; }

    // ─── Phase 8: governance ingest support (FREE, sink-agnostic) ────────────
    //
    // These declare that a sink can ABSORB the deeper governance data classes the
    // pump can produce. They are NOT a license flag — they are a capability fact,
    // exactly like SupportsBulk or SupportsAssignManager. The generator emits the
    // matching steps when (a) the SINK advertises the capability and, for the four
    // data-class ingests, (b) the SOURCE can actually produce the data (an EntraID
    // source). The orchestrator capability-skips a step whose sink lacks the flag.
    //
    // HONEST STATUS today: only the IdentityCenter sink advertises these — it has
    // the dedicated bulk ingest endpoints (/api/objects/licenses/bulk, …) and the
    // membership endpoint (IGroupMembershipEmittingSink). AD / Entra / Emulator /
    // LDAP sinks leave these false (graceful) — they can implement later WITHOUT a
    // generator change. Relationship resolution (manager/owner) reuses the existing
    // SupportsAssignManager / SupportsAssignGroupOwner flags — the Lookup step reads
    // exactly those, so no separate "SupportsManagerResolution" flag is introduced.

    /// <summary>
    /// Sink can ingest Microsoft 365 license assignments (the "license" object
    /// class produced by an EntraID source). IC routes to /api/objects/licenses/bulk.
    /// </summary>
    public bool SupportsLicenseIngest { get; init; }

    /// <summary>
    /// Sink can ingest sign-in log records (the "signinlog" object class produced
    /// by an EntraID source). IC routes to its sign-in-log bulk endpoint.
    /// </summary>
    public bool SupportsSignInLogIngest { get; init; }

    /// <summary>
    /// Sink can ingest M365 usage report rows (the "m365usage" object class produced
    /// by an EntraID source). IC routes to its usage-report bulk endpoint.
    /// </summary>
    public bool SupportsUsageReportIngest { get; init; }

    /// <summary>
    /// Sink can ingest app-role assignments (the "approleassignment" object class
    /// produced by an EntraID source). IC routes to /api/objects/app-role-assignments/bulk.
    /// </summary>
    public bool SupportsAppRoleIngest { get; init; }

    /// <summary>
    /// Sink can absorb group→member edges as a second pass. This is the declarative
    /// twin of implementing <see cref="IGroupMembershipEmittingSink"/>: the generator
    /// reads THIS flag to decide whether to emit the "Sync Group Memberships" step,
    /// and the orchestrator's runtime capture path independently probes the interface
    /// (so the two never disagree — a sink that sets this MUST implement the interface).
    /// </summary>
    public bool SupportsGroupMembership { get; init; }

    public static readonly ConnectorCapabilities Default = new();
}

/// <summary>
/// Phase 5. Result of a provisioning call (<see cref="IConnectorSink.CreateAsync"/>,
/// <see cref="IConnectorSink.MoveAsync"/>, <see cref="IConnectorSink.ResetPasswordAsync"/>).
///
/// Distinct from <see cref="SinkWriteResult"/> because provisioning has a richer
/// outcome vocabulary — specifically, NotSupported (sink doesn't implement) and
/// Accepted (async — caller polls later) which Upsert doesn't surface.
/// </summary>
public sealed class ProvisionResult
{
    public ProvisionOutcome Outcome { get; init; }
    /// <summary>Set when Outcome=Success / Accepted: the assigned external id.</summary>
    public string? ExternalId { get; init; }
    /// <summary>Set when Outcome=Accepted: opaque task handle the caller can poll.</summary>
    public string? TaskId { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProvisionResult Success(string? externalId = null) =>
        new() { Outcome = ProvisionOutcome.Success, ExternalId = externalId };

    public static ProvisionResult Accepted(string taskId, string? externalId = null) =>
        new() { Outcome = ProvisionOutcome.Accepted, TaskId = taskId, ExternalId = externalId };

    public static ProvisionResult Failed(string message) =>
        new() { Outcome = ProvisionOutcome.Failed, ErrorMessage = message };

    public static ProvisionResult NotSupported(string message) =>
        new() { Outcome = ProvisionOutcome.NotSupported, ErrorMessage = message };
}

public enum ProvisionOutcome
{
    Success,
    Accepted,
    Failed,
    NotSupported,
}

/// <summary>
/// Phase 7. Result of <see cref="IConnectorSink.MatchPersonAsync"/>.
/// </summary>
public sealed class PersonMatchResult
{
    /// <summary>Null when no person matched.</summary>
    public string? MatchedIdentityId { get; init; }
    /// <summary>Confidence 0–1. 1.0 = exact UPN/employeeId; lower for fuzzy.</summary>
    public double Confidence { get; init; }
    /// <summary>Short human label of what the match keyed on (e.g. "upn", "email", "employeeId").</summary>
    public string MatchedBy { get; init; } = string.Empty;
    /// <summary>Error message when the match probe itself failed (network etc.).</summary>
    public string? ErrorMessage { get; init; }
    public static PersonMatchResult Miss() => new();
    public static PersonMatchResult Hit(string id, double confidence, string matchedBy) =>
        new() { MatchedIdentityId = id, Confidence = confidence, MatchedBy = matchedBy };
    public static PersonMatchResult Fail(string msg) => new() { ErrorMessage = msg };
}

/// <summary>
/// Phase 7. Result of <see cref="IConnectorSink.CreatePersonAsync"/>.
/// </summary>
public sealed class PersonCreateResult
{
    /// <summary>The newly-created identity's sink-side identifier. Null on failure.</summary>
    public string? CreatedIdentityId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? ErrorMessage { get; init; }
    public static PersonCreateResult Ok(string id) => new() { CreatedIdentityId = id };
    public static PersonCreateResult Fail(string msg) => new() { ErrorMessage = msg };
}

/// <summary>
/// Per-credential-type descriptor exposed by the adapter so the multi-cred UX
/// can render a generic field editor. Phase 2 adds this so a single tenant can
/// hold e.g. both an AWS IAM cred and an AWS SSO cred under different names.
/// </summary>
public sealed class CredentialTypeInfo
{
    /// <summary>Stable key, e.g. "iam", "sso", "scim". Persisted as ConnectionCredentials.CredentialName.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Short UI label.</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>One-line description for the picker.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Ordered field specs the modal will render.</summary>
    public IReadOnlyList<CredentialFieldSpec> Fields { get; init; } = Array.Empty<CredentialFieldSpec>();
}

/// <summary>
/// Phase 6. Declares that a field on the <see cref="Conduit.Core.Models.Tenant"/>
/// entity is required (or carries connector-specific help) for this connector
/// type. <see cref="FieldName"/> matches a property name on Tenant — today only
/// "Domain" is meaningful (AD's LDAP host). Future tenant-level fields would
/// extend this list. The connection form binds field-by-field against this list
/// so adding a new requirement is a one-liner on the adapter.
/// </summary>
public sealed class TenantFieldRequirement
{
    /// <summary>Property name on <c>Tenant</c>. Today: "Domain".</summary>
    public string FieldName { get; init; } = string.Empty;
    /// <summary>True = Save is blocked when empty. False = render help only.</summary>
    public bool Required { get; init; }
    /// <summary>Renders under the field. Connector-specific guidance.</summary>
    public string? HelpText { get; init; }
    /// <summary>Replaces the generic placeholder when set.</summary>
    public string? Placeholder { get; init; }
}

public sealed class CredentialFieldSpec
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
    public bool IsRequired { get; init; }
    public bool IsSecret { get; init; }
    public bool IsMultiline { get; init; }
    /// <summary>Phase 3: renders as a checkbox. Persisted as a JSON bool, default false.</summary>
    public bool IsBoolean { get; init; }
    /// <summary>Optional default for string fields. Booleans default to false.</summary>
    public string? DefaultValue { get; init; }
    /// <summary>
    /// When non-empty, the field renders as a dropdown (&lt;select&gt;) constrained
    /// to these values instead of a free-text input. Used e.g. by the IdentityCenter
    /// connector's table selector (Objects|Identities). Persisted as a JSON string
    /// like any other text field. Ignored when <see cref="IsSecret"/>/<see cref="IsBoolean"/>
    /// /<see cref="IsMultiline"/> is set.
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Opaque source-side resume token. Format is connector-defined (deltaLink, ISO
/// timestamp, USN, page cookie). Conduit only stores and replays it.
/// </summary>
public sealed class SyncCursor
{
    public string Token { get; init; } = string.Empty;
    public DateTime IssuedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Result envelope from <see cref="IConnectorSource.EnumerateAsync"/>. The
/// orchestrator persists <see cref="NewCursor"/> after a successful run so the
/// next run can resume.
/// </summary>
public sealed class SyncEnumerationResult
{
    public IAsyncEnumerable<ConnectorObject> Objects { get; init; } = default!;

    /// <summary>
    /// Resolves the cursor AFTER the stream is consumed (so incremental sources
    /// can stamp it with the highest watermark seen during enumeration). Return
    /// null to skip persistence — orchestrator will then run full next time.
    /// Default = no cursor.
    /// </summary>
    public Func<SyncCursor?> ResolveNewCursor { get; init; } = () => null;

    /// <summary>True if this enumeration was incremental (vs. fresh full).</summary>
    public bool IsIncremental { get; init; }

    /// <summary>
    /// SAFETY-CRITICAL (Phase 2.2 tombstones). Resolved AFTER <see cref="Objects"/>
    /// is fully drained — exactly like <see cref="ResolveNewCursor"/>. Returns TRUE
    /// only when the source enumerator reached its natural end with NO error, NO
    /// cancellation, and NO early exit (e.g. a MaxObjects truncation). Returns FALSE
    /// for any partial / failed / cancelled / truncated read.
    ///
    /// The orchestrator MUST require this to be TRUE before it computes ANY
    /// delete-delta (tombstones). A partial read that yield-break'd on a swallowed
    /// error is indistinguishable from a clean drain by the stream alone — this
    /// sentinel is the ONLY signal that disambiguates them, so it is the linchpin
    /// of delete-detection safety.
    ///
    /// DEFAULT IS FALSE. An adapter that does not explicitly prove a complete read
    /// inherits the safe default and will NEVER trigger tombstoning. Never default
    /// this to true. Set it true only at the enumerator's proven natural terminus.
    /// </summary>
    public Func<bool> WasCompleteRead { get; init; } = () => false;
}

public sealed class ConnectorTestResult
{
    public bool IsSuccessful { get; init; }
    public string Message { get; init; } = string.Empty;
}

public enum SinkWriteOutcome { Created, Updated, Skipped, Failed }

public sealed class SinkWriteResult
{
    public SinkWriteOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>
    /// Phase 4: when a sink submits an async job instead of acting synchronously,
    /// it returns Outcome=Skipped + this descriptor populated. The orchestrator
    /// persists it to SyncRunAsyncJobs for later polling. Null for plain syncs.
    /// </summary>
    public AsyncJobSubmission? AsyncJob { get; init; }
    public static SinkWriteResult Ok(SinkWriteOutcome outcome) => new() { Outcome = outcome };
    public static SinkWriteResult Fail(string msg) => new() { Outcome = SinkWriteOutcome.Failed, ErrorMessage = msg };
    public static SinkWriteResult Pending(AsyncJobSubmission job) => new() { Outcome = SinkWriteOutcome.Skipped, AsyncJob = job };
}

// ─── Phase 4: async-job framework ──────────────────────────────────────────

/// <summary>
/// Phase 4. Some target systems (notably AWS SSO Admin's
/// CreateAccountAssignment) return a request handle instead of completing
/// synchronously. The sink hands back an <see cref="AsyncJobSubmission"/>;
/// the orchestrator persists it; a background poller (<c>AsyncJobPollerService</c>)
/// ticks each pending row and asks the matching adapter to resolve its state.
///
/// SystemType keys the poller back to the right adapter — adapters expose a
/// resolver via <see cref="IConnectorAdapter.CreateAsyncJobResolver"/>.
/// </summary>
public sealed class AsyncJobSubmission
{
    /// <summary>Opaque handle from the target system (e.g. AWS RequestId).</summary>
    public string JobId { get; init; } = string.Empty;
    /// <summary>Short adapter-defined job kind (e.g. "AwsSsoCreateAccountAssignment").</summary>
    public string JobType { get; init; } = string.Empty;
    /// <summary>Source object identifier (orchestrator passes the ConnectorObject.SourceId).</summary>
    public string ObjectExternalId { get; init; } = string.Empty;
    /// <summary>Adapter-specific JSON payload — opaque to Conduit, replayed back to the resolver.</summary>
    public string? PayloadJson { get; init; }
}

public enum AsyncJobState { Pending, Succeeded, Failed }

/// <summary>
/// What the poller writes back to the SyncRunAsyncJobs row.
/// </summary>
public sealed class AsyncJobStatus
{
    public AsyncJobState State { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResultJson { get; init; }
    public static AsyncJobStatus StillPending() => new() { State = AsyncJobState.Pending };
    public static AsyncJobStatus Ok(string? resultJson = null) => new() { State = AsyncJobState.Succeeded, ResultJson = resultJson };
    public static AsyncJobStatus Fail(string msg) => new() { State = AsyncJobState.Failed, ErrorMessage = msg };
}

/// <summary>
/// Adapter-side resolver — created per tenant by the adapter, knows how to
/// turn a JobId + JobType into a current state via the target system's
/// "describe-creation-status" API. Stateless; the poller resolves one at a
/// time so concurrency control is delegated to the AWS/Graph SDK clients.
/// </summary>
public interface IConnectorAsyncJobResolver
{
    /// <summary>Returns true if this resolver handles the given JobType.</summary>
    bool CanResolve(string jobType);

    /// <summary>
    /// Probe the target system for the current state. Implementations should
    /// NOT throw on "still pending" — return <see cref="AsyncJobState.Pending"/>
    /// instead. Throw only on unrecoverable errors; the poller will catch and
    /// re-try next tick.
    /// </summary>
    Task<AsyncJobStatus> PollAsync(
        string jobId,
        string jobType,
        string? payloadJson,
        CancellationToken cancellationToken);
}
