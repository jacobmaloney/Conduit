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

    // ─── Phase 7 person-aware step support ──────────────────────────────────
    /// <summary>Sink implements <see cref="IConnectorSink.MatchPersonAsync"/>.</summary>
    public bool SupportsPersonMatch { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.CreatePersonAsync"/>.</summary>
    public bool SupportsPersonCreate { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.AssignManagerAsync"/>.</summary>
    public bool SupportsAssignManager { get; init; }
    /// <summary>Sink implements <see cref="IConnectorSink.AssignGroupOwnerAsync"/>.</summary>
    public bool SupportsAssignGroupOwner { get; init; }

    public static readonly ConnectorCapabilities Default = new();
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
