using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
// NOTE: do NOT add `using Microsoft.Graph.Users.Delta;` / `using Microsoft.Graph.Groups.Delta;`
// — both namespaces define a sibling `DeltaGetResponse` and importing both makes
// the type name ambiguous. We fully-qualify per-call below.
using GraphGroup = Microsoft.Graph.Models.Group;
using GraphUser = Microsoft.Graph.Models.User;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Microsoft Graph source. Paged enumeration over Users / Groups via OData
/// nextLink. Mirrors IC's GraphQueryService attribute set + AD-compatible
/// aliasing (sn/title/sAMAccountName/etc.) so downstream sinks see consistent
/// names regardless of the upstream system.
///
/// Phase 3: incremental path via Graph delta-query (Users.Delta / Groups.Delta)
/// with @odata.deltaLink persistence. First call (cursor null) issues a fresh
/// delta — Graph paginates with nextLink and emits a deltaLink on the final
/// page. Subsequent calls reuse the deltaLink and only see changes. Deleted
/// objects come back with @removed annotation — surfaced as _deleted=true
/// markers for sink interpretation.
/// </summary>
public sealed class EntraIDSource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<EntraIDSource> _logger;

    public EntraIDSource(Guid tenantId, CredentialProtector protector, ILogger<EntraIDSource> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync();
        var filter = string.IsNullOrWhiteSpace(scope.QueryExpression) ? null : scope.QueryExpression;
        var pageSize = scope.PageSize > 0 && scope.PageSize <= 999 ? scope.PageSize : 999;
        var emitted = 0;

        if (string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var obj in EnumerateGroupsAsync(client, filter, pageSize, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // Phase 4: dedicated manager-refresh enumeration. Graph delta endpoints
        // forbid $expand=manager, so the user-delta path can never resolve manager
        // links. Operators wire a second sync project with ObjectClass="ManagerRefresh"
        // pointed at the same Entra tenant + same downstream sink to keep the
        // manager links fresh on a separate cadence. Emits one ConnectorObject per
        // user containing ONLY {id, objectGuid, manager?} — sinks treat absent
        // attributes as "do not touch" so this is a true partial update.
        if (string.Equals(objectClass, "ManagerRefresh", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var obj in EnumerateManagerRefreshAsync(client, filter, pageSize, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // Phase 4: per-group membership-delta enumeration. Walks the existing group
        // list and, for each, fetches /groups/{id}/members/delta with its own per-
        // group cursor stored in SyncRuns.Cursor (JSON map). Each yielded object
        // has the full reconstructed member list in `members` so the existing
        // full-replace sink path works unchanged.
        if (string.Equals(objectClass, "GroupMemberships", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var obj in EnumerateGroupMembershipsAsync(client, filter, pageSize, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // M365 per-user usage stream. Joins five Graph usage reports by UPN into
        // one ConnectorObject per user. Uses raw HTTP with a bearer token (the
        // Graph SDK report endpoints are awkward) and enforces the anonymization
        // gate. Least-priv scope: Reports.Read.All.
        if (string.Equals(objectClass, M365UsageReportSource.ObjectClassName, StringComparison.OrdinalIgnoreCase))
        {
            var usageSource = new M365UsageReportSource(await CreateCredentialAsync(), _logger);
            await foreach (var obj in usageSource.ReadAsync(scope, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // Sign-in EVENT stream. Pages /auditLogs/signIns over a recent window and
        // emits one ConnectorObject per sign-in. High-volume + append-only; least-
        // priv scope: AuditLog.Read.All. A missing scope warns and yields nothing.
        if (string.Equals(objectClass, EntraSignInLogSource.ObjectClassName, StringComparison.OrdinalIgnoreCase))
        {
            var signInSource = new EntraSignInLogSource(client, _logger);
            await foreach (var obj in signInSource.ReadAsync(scope, cancellationToken))
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // Directory object types beyond User/Group. These already have attribute
        // templates (AttributeTemplateCatalog) and are advertised by
        // SyncProjectGenerator (EntraFull/EntraSecurity) but were previously
        // falling through to the User enumeration. Each routes to a dedicated
        // enumerator that pages the matching Graph collection and emits
        // ObjectClass = the generator's exact native string.
        var directoryStream = DispatchDirectoryClass(objectClass, client, filter, pageSize, cancellationToken);
        if (directoryStream is not null)
        {
            await foreach (var obj in directoryStream)
            {
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
                emitted++;
                yield return obj;
            }
            yield break;
        }

        // Default: User
        await foreach (var obj in EnumerateUsersAsync(client, filter, pageSize, cancellationToken))
        {
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;
            emitted++;
            yield return obj;
        }
    }

    /// <summary>
    /// Routes the 8 extended Entra directory object classes to their dedicated
    /// enumerator. Returns null when objectClass is none of them (caller falls
    /// through to the User default). Matching is OrdinalIgnoreCase against the
    /// generator's native strings; the emitted ObjectClass preserves the
    /// generator's exact camelCase (e.g. "servicePrincipal", "oAuth2PermissionGrant")
    /// so the wizard &lt;select&gt; binds without crashing.
    /// </summary>
    private IAsyncEnumerable<ConnectorObject>? DispatchDirectoryClass(
        string objectClass, GraphServiceClient client, string? filter, int pageSize,
        CancellationToken cancellationToken)
    {
        if (string.Equals(objectClass, "application", StringComparison.OrdinalIgnoreCase))
            return EnumerateApplicationsAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "servicePrincipal", StringComparison.OrdinalIgnoreCase))
            return EnumerateServicePrincipalsAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "directoryRole", StringComparison.OrdinalIgnoreCase))
            return EnumerateDirectoryRolesAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "device", StringComparison.OrdinalIgnoreCase))
            return EnumerateDevicesAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "administrativeUnit", StringComparison.OrdinalIgnoreCase))
            return EnumerateAdministrativeUnitsAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "conditionalAccessPolicy", StringComparison.OrdinalIgnoreCase))
            return EnumerateConditionalAccessPoliciesAsync(client, pageSize, cancellationToken);
        if (string.Equals(objectClass, "oAuth2PermissionGrant", StringComparison.OrdinalIgnoreCase))
            return EnumerateOAuth2PermissionGrantsAsync(client, filter, pageSize, cancellationToken);
        if (string.Equals(objectClass, "domain", StringComparison.OrdinalIgnoreCase))
            return EnumerateDomainsAsync(client, cancellationToken);
        return null;
    }

    /// <summary>
    /// Phase 4: enumerate all users with $expand=manager($select=id). Emits one
    /// ConnectorObject per user containing only {id, objectGuid, manager?}. Users
    /// with no manager get `manager=null` so the sink can choose to clear; users
    /// with a manager get the manager's objectId. Cheaper than the full user
    /// enumeration because we strip the select to just identity-shaped fields.
    /// </summary>
    private async IAsyncEnumerable<ConnectorObject> EnumerateManagerRefreshAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await client.Users.GetAsync(req =>
        {
            req.QueryParameters.Top = pageSize;
            req.QueryParameters.Select = new[] { "id", "userPrincipalName" };
            req.QueryParameters.Expand = new[] { "manager($select=id)" };
            if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
        }, cancellationToken);

        while (page?.Value != null)
        {
            foreach (var u in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(u.Id)) continue;
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = u.Id,
                    ["objectGuid"] = u.Id
                };
                if (!string.IsNullOrEmpty(u.UserPrincipalName)) attrs["userPrincipalName"] = u.UserPrincipalName;
                attrs["manager"] = u.Manager is { Id: { Length: > 0 } mid } ? (object?)mid : null;
                yield return new ConnectorObject
                {
                    SourceId = u.Id,
                    ObjectClass = "User",
                    Attributes = attrs
                };
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Users.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Phase 4: per-group membership enumeration. For each enumerated group we
    /// fetch the full member list (cheap for incremental visibility — Graph has
    /// /groups/{id}/members/delta but its cursor would have to be persisted per-
    /// group, requiring a multi-cursor schema). We picked the simpler full-replace
    /// path here: each yielded group carries `members` so the existing sink
    /// reconciliation logic works unchanged.
    /// </summary>
    private async IAsyncEnumerable<ConnectorObject> EnumerateGroupMembershipsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await client.Groups.GetAsync(req =>
        {
            req.QueryParameters.Top = pageSize;
            req.QueryParameters.Select = new[] { "id", "displayName" };
            if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
        }, cancellationToken);

        while (page?.Value != null)
        {
            foreach (var g in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(g.Id)) continue;

                var members = await TryGetGroupMembersAsync(client, g.Id, cancellationToken);
                var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = g.Id,
                    ["objectGuid"] = g.Id,
                    ["members"] = members
                };
                if (!string.IsNullOrEmpty(g.DisplayName)) attrs["displayName"] = g.DisplayName;
                yield return new ConnectorObject
                {
                    SourceId = g.Id,
                    ObjectClass = "Group",
                    Attributes = attrs
                };
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Groups.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Phase 3 incremental enumeration. When cursor is null we initiate a fresh
    /// delta call; Graph returns a deltaLink on the LAST page which we capture
    /// via the watermark holder and persist for the next run. When cursor is
    /// present we resume with WithUrl(cursor.Token).
    ///
    /// Delta has a hard restriction: $select limits, $expand=manager unavailable,
    /// $filter unsupported beyond a small whitelist. We accept the reduced
    /// fidelity in exchange for incremental — a periodic full ReadAsync can be
    /// scheduled separately to backfill the omitted attributes.
    /// </summary>
    public Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        var holder = new DeltaLinkHolder();
        var isIncremental = cursor is not null && !string.IsNullOrWhiteSpace(cursor.Token);
        // Complete-read sentinel. Starts FALSE. The drain-wrapper below flips it true
        // ONLY after the inner stream falls off its natural terminus with no exception
        // and no cancellation. A throw or a cancellation never reaches the set-site, so
        // the orchestrator never tombstones (and the membership second pass never
        // mis-reports) against a partial read. Mirrors ActiveDirectorySource.ReadCompletion.
        var completion = new ReadCompletion();
        IAsyncEnumerable<ConnectorObject> stream;

        if (string.Equals(objectClass, "Group", StringComparison.OrdinalIgnoreCase))
        {
            stream = EnumerateGroupsDeltaAsync(cursor?.Token, holder, cancellationToken);
        }
        else if (string.Equals(objectClass, M365UsageReportSource.ObjectClassName, StringComparison.OrdinalIgnoreCase))
        {
            // Usage reports have no delta endpoint — always full-read, never advertise a cursor.
            return EnumerateFullForExtendedClassAsync(objectClass, scope, cancellationToken);
        }
        else if (string.Equals(objectClass, EntraSignInLogSource.ObjectClassName, StringComparison.OrdinalIgnoreCase))
        {
            // Sign-in events have no delta endpoint — windowed full-read each run,
            // never advertise a cursor (the windowed read is the incremental story).
            return EnumerateFullForExtendedClassAsync(objectClass, scope, cancellationToken);
        }
        else if (IsExtendedDirectoryClass(objectClass))
        {
            // The 8 extended directory classes do not implement delta cursors in
            // this connector. directoryRole / conditionalAccessPolicy /
            // oAuth2PermissionGrant / domain have NO Graph delta endpoint at all;
            // application / servicePrincipal / device / administrativeUnit do but
            // we deliberately full-read each run rather than persist 8 separate
            // cursors now. Route the delta path to the same full enumerator and
            // never advertise a new cursor (IsIncremental stays false).
            return EnumerateFullForExtendedClassAsync(objectClass, scope, cancellationToken);
        }
        else
        {
            stream = EnumerateUsersDeltaAsync(cursor?.Token, holder, cancellationToken);
        }

        return Task.FromResult(new SyncEnumerationResult
        {
            // The delta enumerators yield-break only at their natural terminus (delta
            // link reached or empty next link) or propagate a throw; they have no
            // MaxObjects truncation, so a clean full drain is a complete read.
            Objects = DrainWithCompletionAsync(stream, completion, cancellationToken),
            ResolveNewCursor = () => string.IsNullOrEmpty(holder.DeltaLink)
                ? null
                : new SyncCursor { Token = holder.DeltaLink! },
            IsIncremental = isIncremental,
            WasCompleteRead = () => completion.IsComplete
        });
    }

    /// <summary>
    /// Mutable complete-read flag threaded through the chosen enumerator. Defaults to
    /// FALSE; only the natural terminus of the active read path sets it true. A throw
    /// or a cancellation propagates out of the drain-wrapper before the set-site, so
    /// the flag faithfully reports a clean, full drain — the precondition the
    /// orchestrator requires before any delete-delta. Mirrors AD's ReadCompletion.
    /// </summary>
    private sealed class ReadCompletion
    {
        public bool IsComplete { get; set; }
    }

    /// <summary>
    /// Wraps an inner object stream that has NO MaxObjects truncation (the delta
    /// enumerators) and flips <paramref name="completion"/> true ONLY after it drains
    /// to its natural end. If the inner stream throws or the consumer cancels, the
    /// set-site is never reached and the flag stays false.
    /// </summary>
    private static async IAsyncEnumerable<ConnectorObject> DrainWithCompletionAsync(
        IAsyncEnumerable<ConnectorObject> inner,
        ReadCompletion completion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var obj in inner.WithCancellation(cancellationToken))
            yield return obj;
        completion.IsComplete = true;
    }

    /// <summary>
    /// Wraps the full-read stream (which routes through <see cref="ReadAsync"/> and so
    /// CAN truncate on scope.MaxObjects) and flips <paramref name="completion"/> true
    /// only when the read drained naturally AND was not truncated by the cap. A
    /// MaxObjects truncation inside ReadAsync is a clean yield-break that would
    /// otherwise look like a natural end, so count emitted objects and treat hitting
    /// the cap as a truncated (incomplete) read — mirroring AD's MaxObjects handling.
    /// </summary>
    private static async IAsyncEnumerable<ConnectorObject> DrainFullWithCompletionAsync(
        IAsyncEnumerable<ConnectorObject> inner,
        SyncProjectScope scope,
        ReadCompletion completion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long emitted = 0;
        await foreach (var obj in inner.WithCancellation(cancellationToken))
        {
            emitted++;
            yield return obj;
        }
        var truncated = scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value;
        if (!truncated)
            completion.IsComplete = true;
    }

    /// <summary>
    /// True for the 8 directory classes that have no delta implementation here
    /// and must be full-read on every run.
    /// </summary>
    internal static bool IsExtendedDirectoryClass(string objectClass) =>
        string.Equals(objectClass, "application", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "servicePrincipal", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "directoryRole", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "device", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "administrativeUnit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "conditionalAccessPolicy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "oAuth2PermissionGrant", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(objectClass, "domain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Delta-path entry for the extended directory classes: defers to the full
    /// ReadAsync enumeration and reports IsIncremental=false with no new cursor.
    /// </summary>
    private Task<SyncEnumerationResult> EnumerateFullForExtendedClassAsync(
        string objectClass, SyncProjectScope scope, CancellationToken cancellationToken)
    {
        var completion = new ReadCompletion();
        return Task.FromResult(new SyncEnumerationResult
        {
            Objects = DrainFullWithCompletionAsync(
                ReadAsync(objectClass, scope, cancellationToken), scope, completion, cancellationToken),
            ResolveNewCursor = () => null,
            IsIncremental = false,
            WasCompleteRead = () => completion.IsComplete
        });
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var creds = await EntraIDCredentialReader.ReadAsync(_protector, _tenantId);
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'entraid' credential stored." };

            var client = await CreateClientAsync();
            // Probe — get one user.
            var probe = await client.Users.GetAsync(req =>
            {
                req.QueryParameters.Top = 1;
                req.QueryParameters.Select = new[] { "id", "displayName" };
            }, cancellationToken);

            var sample = probe?.Value?.Count > 0 ? probe.Value[0].DisplayName : "(no users)";
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Connected to tenant {creds.TenantId}. Sample: {sample}."
            };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── enumeration ──────────────────────────────────────────────────────

    private async IAsyncEnumerable<ConnectorObject> EnumerateUsersAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await client.Users.GetAsync(req =>
        {
            req.QueryParameters.Top = pageSize;
            req.QueryParameters.Select = UserSelectFields;
            req.QueryParameters.Expand = new[] { "manager($select=id)" };
            if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
        }, cancellationToken);

        while (page?.Value != null)
        {
            foreach (var u in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ConvertUser(u);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Users.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateGroupsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await client.Groups.GetAsync(req =>
        {
            req.QueryParameters.Top = pageSize;
            req.QueryParameters.Select = GroupSelectFields;
            if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
        }, cancellationToken);

        while (page?.Value != null)
        {
            foreach (var g in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var members = await TryGetGroupMembersAsync(client, g.Id, cancellationToken);
                yield return ConvertGroup(g, members);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Groups.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    // ─── extended directory object enumerators ────────────────────────────
    // Each pages the matching Graph collection, projects ONLY the template
    // attributes ($select), and emits ObjectClass = the generator's native
    // camelCase string. A per-class 403 (missing app-registration scope) is
    // swallowed with a clear WARNING and yields nothing rather than failing the
    // whole run; any non-403 Graph error propagates.

    private async IAsyncEnumerable<ConnectorObject> EnumerateApplicationsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplicationCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Applications.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = ApplicationSelectFields;
                if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
            }, cancellationToken),
            r => page = r, "application", "Application.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var a in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(a.Id)) continue;
                yield return ConvertApplication(a);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Applications.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateServicePrincipalsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ServicePrincipalCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.ServicePrincipals.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = ServicePrincipalSelectFields;
                if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
            }, cancellationToken),
            r => page = r, "servicePrincipal", "Application.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var sp in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(sp.Id)) continue;
                yield return ConvertServicePrincipal(sp);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.ServicePrincipals.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    // Only ACTIVATED directory roles return rows from /directoryRoles.
    private async IAsyncEnumerable<ConnectorObject> EnumerateDirectoryRolesAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DirectoryRoleCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.DirectoryRoles.GetAsync(req =>
            {
                req.QueryParameters.Select = DirectoryRoleSelectFields;
            }, cancellationToken),
            r => page = r, "directoryRole", "RoleManagement.Read.Directory"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var r in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(r.Id)) continue;
                yield return ConvertDirectoryRole(r);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.DirectoryRoles.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateDevicesAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DeviceCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Devices.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = DeviceSelectFields;
                if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
            }, cancellationToken),
            r => page = r, "device", "Device.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var d in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(d.Id)) continue;
                yield return ConvertDevice(d);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Devices.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    // AdministrativeUnits hang off /directory, not the top-level client.
    private async IAsyncEnumerable<ConnectorObject> EnumerateAdministrativeUnitsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AdministrativeUnitCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Directory.AdministrativeUnits.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = AdministrativeUnitSelectFields;
                if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
            }, cancellationToken),
            r => page = r, "administrativeUnit", "AdministrativeUnit.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var au in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(au.Id)) continue;
                yield return ConvertAdministrativeUnit(au);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Directory.AdministrativeUnits.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateConditionalAccessPoliciesAsync(
        GraphServiceClient client, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ConditionalAccessPolicyCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Identity.ConditionalAccess.Policies.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = ConditionalAccessPolicySelectFields;
            }, cancellationToken),
            r => page = r, "conditionalAccessPolicy", "Policy.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var p in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(p.Id)) continue;
                yield return ConvertConditionalAccessPolicy(p);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Identity.ConditionalAccess.Policies.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateOAuth2PermissionGrantsAsync(
        GraphServiceClient client, string? filter, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        OAuth2PermissionGrantCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Oauth2PermissionGrants.GetAsync(req =>
            {
                req.QueryParameters.Top = pageSize;
                req.QueryParameters.Select = OAuth2PermissionGrantSelectFields;
                if (!string.IsNullOrWhiteSpace(filter)) req.QueryParameters.Filter = filter;
            }, cancellationToken),
            r => page = r, "oAuth2PermissionGrant", "Directory.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var g in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(g.Id)) continue;
                yield return ConvertOAuth2PermissionGrant(g);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Oauth2PermissionGrants.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    // Domain's id IS the domain name (Graph keys domains by name). No $top/$filter
    // — the domains collection is small and unpaged in practice.
    private async IAsyncEnumerable<ConnectorObject> EnumerateDomainsAsync(
        GraphServiceClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DomainCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => client.Domains.GetAsync(req =>
            {
                req.QueryParameters.Select = DomainSelectFields;
            }, cancellationToken),
            r => page = r, "domain", "Domain.Read.All"))
            yield break;

        while (page?.Value != null)
        {
            foreach (var d in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(d.Id)) continue;
                yield return ConvertDomain(d);
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Domains.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Runs the first-page Graph fetch and assigns the result via <paramref name="assign"/>.
    /// Returns true if the caller should continue paging. A 403 / Forbidden /
    /// Authorization_RequestDenied ODataError (app registration lacks the scope)
    /// is logged at Warning with the scope hint and returns false (yield nothing);
    /// any other error propagates. Never logs token/secret material.
    /// </summary>
    private async Task<bool> TryFirstPageAsync<TResponse>(
        Func<Task<TResponse?>> fetch, Action<TResponse?> assign,
        string objectClass, string scopeHint)
    {
        try
        {
            assign(await fetch());
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
        {
            _logger.LogWarning(
                "EntraID: skipping class {ObjectClass} — app registration lacks scope {Scope} (403)",
                objectClass, scopeHint);
            return false;
        }
    }

    private static bool IsForbidden(Microsoft.Graph.Models.ODataErrors.ODataError ex)
    {
        if (ex.ResponseStatusCode == 403) return true;
        var code = ex.Error?.Code;
        return string.Equals(code, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateUsersDeltaAsync(
        string? deltaLink,
        DeltaLinkHolder holder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync();
        Microsoft.Graph.Users.Delta.DeltaGetResponse? page;

        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            page = await client.Users.Delta.WithUrl(deltaLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
        }
        else
        {
            page = await client.Users.Delta.GetAsDeltaGetResponseAsync(req =>
            {
                req.QueryParameters.Select = UserDeltaSelectFields;
            }, cancellationToken);
        }

        while (page?.Value != null)
        {
            foreach (var u in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ConvertUserDelta(u);
            }

            // Last page in delta sequence carries deltaLink; intermediate pages carry nextLink.
            if (!string.IsNullOrEmpty(page.OdataDeltaLink))
            {
                holder.DeltaLink = page.OdataDeltaLink;
                yield break;
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Users.Delta.WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
        }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateGroupsDeltaAsync(
        string? deltaLink,
        DeltaLinkHolder holder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync();
        Microsoft.Graph.Groups.Delta.DeltaGetResponse? page;

        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            page = await client.Groups.Delta.WithUrl(deltaLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
        }
        else
        {
            page = await client.Groups.Delta.GetAsDeltaGetResponseAsync(req =>
            {
                req.QueryParameters.Select = GroupDeltaSelectFields;
            }, cancellationToken);
        }

        while (page?.Value != null)
        {
            foreach (var g in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Delta surfaces members as a changing list (member@delta) but the SDK
                // collapses it, so fetch the live roster per group (N+1, mirrors the
                // legacy full path) to stamp attrs["members"] for membership capture.
                // Removed/tombstoned groups are being deleted — skip the member fetch.
                var members = IsRemoved(g.AdditionalData)
                    ? new List<string>()
                    : await TryGetGroupMembersAsync(client, g.Id, cancellationToken);
                yield return ConvertGroupDelta(g, members);
            }

            if (!string.IsNullOrEmpty(page.OdataDeltaLink))
            {
                holder.DeltaLink = page.OdataDeltaLink;
                yield break;
            }
            if (string.IsNullOrEmpty(page.OdataNextLink)) yield break;
            page = await client.Groups.Delta.WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
        }
    }

    private static async Task<List<string>> TryGetGroupMembersAsync(
        GraphServiceClient client, string? groupId, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        if (string.IsNullOrEmpty(groupId)) return ids;
        try
        {
            var page = await client.Groups[groupId].Members.GetAsync(req =>
            {
                req.QueryParameters.Top = 999;
                req.QueryParameters.Select = new[] { "id" };
            }, cancellationToken);
            while (page?.Value != null)
            {
                foreach (var m in page.Value)
                    if (!string.IsNullOrEmpty(m.Id)) ids.Add(m.Id);
                if (string.IsNullOrEmpty(page.OdataNextLink)) break;
                page = await client.Groups[groupId].Members.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
            }
        }
        catch { /* swallow — membership is best-effort */ }
        return ids;
    }

    // ─── converters (parity with IC GraphQueryService) ────────────────────

    private static readonly string[] UserSelectFields = new[]
    {
        "id", "displayName", "userPrincipalName", "mail", "givenName", "surname",
        "department", "jobTitle", "companyName", "officeLocation",
        "mobilePhone", "businessPhones", "streetAddress", "city", "state",
        "postalCode", "country", "employeeId", "employeeType",
        "accountEnabled", "onPremisesSamAccountName", "onPremisesDistinguishedName",
        "createdDateTime", "mailNickname", "proxyAddresses", "faxNumber",
        // signInActivity requires the AuditLog.Read.All app scope (consented) and
        // Entra ID P1+ — if the scope were missing Graph 403s the whole user read.
        "signInActivity"
    };

    // Delta supports a narrower select set. Graph rejects some fields (e.g.
    // businessPhones is multi-value tracked separately, proxyAddresses likewise).
    // Keep this subset honest — full hydrate happens on the scheduled full pass.
    private static readonly string[] UserDeltaSelectFields = new[]
    {
        "id", "displayName", "userPrincipalName", "mail", "givenName", "surname",
        "department", "jobTitle", "companyName", "officeLocation",
        "mobilePhone", "employeeId", "accountEnabled",
        "onPremisesSamAccountName", "mailNickname"
        // signInActivity is NOT supported on /users/delta $select (Graph rejects it) — it flows on the full read only.
    };

    private static readonly string[] GroupSelectFields = new[]
    {
        "id", "displayName", "description", "mail", "mailEnabled",
        "securityEnabled", "groupTypes", "createdDateTime",
        "onPremisesSamAccountName", "onPremisesDistinguishedName",
        "proxyAddresses", "mailNickname"
    };

    private static readonly string[] GroupDeltaSelectFields = new[]
    {
        "id", "displayName", "description", "mail", "mailEnabled",
        "securityEnabled", "onPremisesSamAccountName", "mailNickname"
    };

    // ─── extended directory $select sets (template attributes only) ────────
    // Deliberately NO keyCredentials / passwordCredentials on application or
    // servicePrincipal — secrets are never selected, emitted, or logged.
    private static readonly string[] ApplicationSelectFields = new[]
    {
        "id", "displayName", "appId", "signInAudience", "publisherDomain",
        "description", "identifierUris", "tags", "createdDateTime"
    };

    private static readonly string[] ServicePrincipalSelectFields = new[]
    {
        "id", "displayName", "appId", "servicePrincipalType", "appDisplayName",
        "servicePrincipalNames", "accountEnabled"
        // NOTE: createdDateTime (template WhenCreated) is not a property on the
        // Graph 5.61 ServicePrincipal model — omitted; template field stays unset.
    };

    private static readonly string[] DirectoryRoleSelectFields = new[]
    {
        "id", "displayName", "description", "roleTemplateId"
    };

    private static readonly string[] DeviceSelectFields = new[]
    {
        "id", "displayName", "deviceId", "operatingSystem", "operatingSystemVersion",
        "trustType", "managementType", "manufacturer", "model",
        "isManaged", "isCompliant", "accountEnabled"
        // NOTE: lastSignInDateTime / createdDateTime (template LastLogonTimestamp /
        // WhenCreated) are not directly selectable top-level Device properties on
        // the Graph 5.61 model (the entity exposes approximateLastSignInDateTime /
        // registrationDateTime instead) — omitted; those template fields stay unset.
    };

    private static readonly string[] AdministrativeUnitSelectFields = new[]
    {
        "id", "displayName", "description", "visibility"
    };

    private static readonly string[] ConditionalAccessPolicySelectFields = new[]
    {
        "id", "displayName", "state", "createdDateTime", "modifiedDateTime"
    };

    private static readonly string[] OAuth2PermissionGrantSelectFields = new[]
    {
        "id", "clientId", "consentType", "principalId", "resourceId", "scope"
    };

    private static readonly string[] DomainSelectFields = new[]
    {
        "id", "authenticationType", "isDefault", "isVerified",
        "isInitial", "supportedServices"
    };

    private static ConnectorObject ConvertUser(GraphUser u)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "user"
        };

        if (!string.IsNullOrEmpty(u.Id))
        {
            attrs["id"] = u.Id;
            attrs["objectGuid"] = u.Id;
        }

        Set(attrs, "displayName", u.DisplayName);
        Set(attrs, "userPrincipalName", u.UserPrincipalName);
        Set(attrs, "mail", u.Mail);
        Set(attrs, "email", u.Mail);
        Set(attrs, "givenName", u.GivenName);
        Set(attrs, "surname", u.Surname);
        Set(attrs, "sn", u.Surname);                     // AD alias
        Set(attrs, "familyName", u.Surname);             // SCIM alias
        Set(attrs, "department", u.Department);
        Set(attrs, "jobTitle", u.JobTitle);
        Set(attrs, "title", u.JobTitle);                 // AD alias
        Set(attrs, "companyName", u.CompanyName);
        Set(attrs, "company", u.CompanyName);            // AD alias
        Set(attrs, "officeLocation", u.OfficeLocation);
        Set(attrs, "physicalDeliveryOfficeName", u.OfficeLocation);
        Set(attrs, "mobilePhone", u.MobilePhone);
        Set(attrs, "mobile", u.MobilePhone);             // AD alias
        if (u.BusinessPhones is { Count: > 0 })
        {
            attrs["telephoneNumber"] = u.BusinessPhones[0];
            attrs["businessPhones"] = string.Join(";", u.BusinessPhones);
        }
        Set(attrs, "faxNumber", u.FaxNumber);
        Set(attrs, "facsimileTelephoneNumber", u.FaxNumber);
        Set(attrs, "streetAddress", u.StreetAddress);
        Set(attrs, "city", u.City);
        Set(attrs, "l", u.City);
        Set(attrs, "state", u.State);
        Set(attrs, "st", u.State);
        Set(attrs, "postalCode", u.PostalCode);
        Set(attrs, "country", u.Country);
        Set(attrs, "co", u.Country);
        Set(attrs, "employeeId", u.EmployeeId);
        Set(attrs, "employeeNumber", u.EmployeeId);
        Set(attrs, "employeeType", u.EmployeeType);
        Set(attrs, "mailNickname", u.MailNickname);
        Set(attrs, "onPremisesSamAccountName", u.OnPremisesSamAccountName);
        Set(attrs, "sAMAccountName", u.OnPremisesSamAccountName);
        Set(attrs, "userName", u.OnPremisesSamAccountName ?? u.UserPrincipalName);
        Set(attrs, "onPremisesDistinguishedName", u.OnPremisesDistinguishedName);
        if (!string.IsNullOrEmpty(u.OnPremisesDistinguishedName))
            attrs["distinguishedName"] = u.OnPremisesDistinguishedName;
        var cn = u.MailNickname ?? u.DisplayName ?? u.UserPrincipalName;
        if (!string.IsNullOrEmpty(cn)) attrs["cn"] = cn;
        if (u.AccountEnabled.HasValue)
        {
            attrs["accountEnabled"] = u.AccountEnabled.Value;
            attrs["active"] = u.AccountEnabled.Value;
            attrs["userAccountControl"] = u.AccountEnabled.Value ? 512 : 514;
        }
        if (u.Manager is { Id: { Length: > 0 } managerId })
            attrs["manager"] = managerId;
        if (u.ProxyAddresses is { Count: > 0 })
            attrs["proxyAddresses"] = string.Join(";", u.ProxyAddresses);
        if (u.CreatedDateTime.HasValue)
            attrs["whenCreated"] = u.CreatedDateTime.Value.ToString("o");
        Set(attrs, "lastSignInDateTime", u.SignInActivity?.LastSignInDateTime?.ToString("o"));
        Set(attrs, "lastNonInteractiveSignInDateTime", u.SignInActivity?.LastNonInteractiveSignInDateTime?.ToString("o"));

        return new ConnectorObject
        {
            SourceId = u.Id ?? string.Empty,
            ObjectClass = "User",
            Attributes = attrs
        };
    }

    /// <summary>
    /// Delta variant — same attribute mapping but checks AdditionalData for the
    /// @removed annotation Graph emits when an object is deleted/soft-deleted in
    /// the source. Emits a sentinel _deleted=true so sinks know to DELETE.
    /// </summary>
    private static ConnectorObject ConvertUserDelta(GraphUser u)
    {
        var obj = ConvertUser(u);
        if (IsRemoved(u.AdditionalData))
        {
            obj.Attributes["_deleted"] = true;
        }
        return obj;
    }

    private static ConnectorObject ConvertGroup(GraphGroup g, List<string> memberIds)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = "group"
        };
        if (!string.IsNullOrEmpty(g.Id))
        {
            attrs["id"] = g.Id;
            attrs["objectGuid"] = g.Id;
        }
        Set(attrs, "displayName", g.DisplayName);
        Set(attrs, "description", g.Description);
        Set(attrs, "mail", g.Mail);
        Set(attrs, "mailNickname", g.MailNickname);
        var cn = g.MailNickname ?? g.DisplayName;
        if (!string.IsNullOrEmpty(cn)) attrs["cn"] = cn;
        if (g.SecurityEnabled.HasValue)
        {
            attrs["securityEnabled"] = g.SecurityEnabled.Value;
            attrs["groupType"] = g.SecurityEnabled.Value ? -2147483646 : 8;
            attrs["Type"] = g.SecurityEnabled.Value ? "Security" : "Distribution";
        }
        if (g.GroupTypes is { Count: > 0 })
            attrs["groupTypes"] = string.Join(";", g.GroupTypes);
        if (g.MailEnabled.HasValue)
            attrs["mailEnabled"] = g.MailEnabled.Value;
        Set(attrs, "onPremisesSamAccountName", g.OnPremisesSamAccountName);
        Set(attrs, "sAMAccountName", g.OnPremisesSamAccountName);
        if (g.ProxyAddresses is { Count: > 0 })
            attrs["proxyAddresses"] = string.Join(";", g.ProxyAddresses);
        if (g.CreatedDateTime.HasValue)
            attrs["whenCreated"] = g.CreatedDateTime.Value.ToString("o");
        if (memberIds.Count > 0)
            attrs["members"] = memberIds;

        return new ConnectorObject
        {
            SourceId = g.Id ?? string.Empty,
            ObjectClass = "Group",
            Attributes = attrs
        };
    }

    private static ConnectorObject ConvertGroupDelta(GraphGroup g, List<string> memberIds)
    {
        var obj = ConvertGroup(g, memberIds);
        if (IsRemoved(g.AdditionalData))
        {
            obj.Attributes["_deleted"] = true;
        }
        return obj;
    }

    // ─── extended directory converters ────────────────────────────────────
    // SourceId is the directory object id for every type (stable across runs so
    // re-runs UPDATE, not duplicate). For application/servicePrincipal that is
    // `id`, NOT appId. For domain `id` is the domain name (Graph's natural key).
    // Attributes["objectClass"] mirrors the generator's native camelCase string.

    private static ConnectorObject ConvertApplication(Application a)
    {
        var attrs = NewDirectoryAttrs("application", a.Id);
        Set(attrs, "displayName", a.DisplayName);
        Set(attrs, "appId", a.AppId);
        Set(attrs, "signInAudience", a.SignInAudience);
        Set(attrs, "publisherDomain", a.PublisherDomain);
        Set(attrs, "description", a.Description);
        if (a.IdentifierUris is { Count: > 0 })
            attrs["identifierUris"] = string.Join(";", a.IdentifierUris);
        if (a.Tags is { Count: > 0 })
            attrs["tags"] = string.Join(";", a.Tags);
        if (a.CreatedDateTime.HasValue)
            attrs["createdDateTime"] = a.CreatedDateTime.Value.ToString("o");
        return DirectoryObject("application", a.Id, attrs);
    }

    private static ConnectorObject ConvertServicePrincipal(ServicePrincipal sp)
    {
        var attrs = NewDirectoryAttrs("servicePrincipal", sp.Id);
        Set(attrs, "displayName", sp.DisplayName);
        Set(attrs, "appId", sp.AppId);
        Set(attrs, "servicePrincipalType", sp.ServicePrincipalType);
        Set(attrs, "appDisplayName", sp.AppDisplayName);
        if (sp.ServicePrincipalNames is { Count: > 0 })
            attrs["servicePrincipalNames"] = string.Join(";", sp.ServicePrincipalNames);
        if (sp.AccountEnabled.HasValue)
            attrs["accountEnabled"] = sp.AccountEnabled.Value;
        return DirectoryObject("servicePrincipal", sp.Id, attrs);
    }

    private static ConnectorObject ConvertDirectoryRole(DirectoryRole r)
    {
        var attrs = NewDirectoryAttrs("directoryRole", r.Id);
        Set(attrs, "displayName", r.DisplayName);
        Set(attrs, "description", r.Description);
        Set(attrs, "roleTemplateId", r.RoleTemplateId);
        return DirectoryObject("directoryRole", r.Id, attrs);
    }

    private static ConnectorObject ConvertDevice(Device d)
    {
        var attrs = NewDirectoryAttrs("device", d.Id);
        Set(attrs, "displayName", d.DisplayName);
        Set(attrs, "deviceId", d.DeviceId);
        Set(attrs, "operatingSystem", d.OperatingSystem);
        Set(attrs, "operatingSystemVersion", d.OperatingSystemVersion);
        Set(attrs, "trustType", d.TrustType);
        Set(attrs, "manufacturer", d.Manufacturer);
        Set(attrs, "model", d.Model);
        if (d.IsManaged.HasValue) attrs["isManaged"] = d.IsManaged.Value;
        if (d.IsCompliant.HasValue) attrs["isCompliant"] = d.IsCompliant.Value;
        if (d.AccountEnabled.HasValue) attrs["accountEnabled"] = d.AccountEnabled.Value;
        return DirectoryObject("device", d.Id, attrs);
    }

    private static ConnectorObject ConvertAdministrativeUnit(AdministrativeUnit au)
    {
        var attrs = NewDirectoryAttrs("administrativeUnit", au.Id);
        Set(attrs, "displayName", au.DisplayName);
        Set(attrs, "description", au.Description);
        Set(attrs, "visibility", au.Visibility);
        return DirectoryObject("administrativeUnit", au.Id, attrs);
    }

    private static ConnectorObject ConvertConditionalAccessPolicy(ConditionalAccessPolicy p)
    {
        var attrs = NewDirectoryAttrs("conditionalAccessPolicy", p.Id);
        Set(attrs, "displayName", p.DisplayName);
        if (p.State.HasValue) attrs["state"] = p.State.Value.ToString();
        if (p.CreatedDateTime.HasValue)
            attrs["createdDateTime"] = p.CreatedDateTime.Value.ToString("o");
        if (p.ModifiedDateTime.HasValue)
            attrs["modifiedDateTime"] = p.ModifiedDateTime.Value.ToString("o");
        return DirectoryObject("conditionalAccessPolicy", p.Id, attrs);
    }

    // No displayName on this Graph type — template's DisplayName stays unset.
    private static ConnectorObject ConvertOAuth2PermissionGrant(OAuth2PermissionGrant g)
    {
        var attrs = NewDirectoryAttrs("oAuth2PermissionGrant", g.Id);
        Set(attrs, "clientId", g.ClientId);
        Set(attrs, "consentType", g.ConsentType);
        Set(attrs, "principalId", g.PrincipalId);
        Set(attrs, "resourceId", g.ResourceId);
        Set(attrs, "scope", g.Scope);
        return DirectoryObject("oAuth2PermissionGrant", g.Id, attrs);
    }

    // Domain's id IS the domain name; mirror it to displayName so the template's
    // DisplayName mapping has a value (the Graph type has no displayName).
    private static ConnectorObject ConvertDomain(Domain d)
    {
        var attrs = NewDirectoryAttrs("domain", d.Id);
        Set(attrs, "displayName", d.Id);
        Set(attrs, "authenticationType", d.AuthenticationType);
        if (d.IsDefault.HasValue) attrs["isDefault"] = d.IsDefault.Value;
        if (d.IsVerified.HasValue) attrs["isVerified"] = d.IsVerified.Value;
        if (d.IsInitial.HasValue) attrs["isInitial"] = d.IsInitial.Value;
        if (d.SupportedServices is { Count: > 0 })
            attrs["supportedServices"] = string.Join(";", d.SupportedServices);
        return DirectoryObject("domain", d.Id, attrs);
    }

    private static Dictionary<string, object?> NewDirectoryAttrs(string nativeClass, string? id)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = nativeClass
        };
        if (!string.IsNullOrEmpty(id)) attrs["id"] = id;
        return attrs;
    }

    private static ConnectorObject DirectoryObject(string nativeClass, string? id, Dictionary<string, object?> attrs) =>
        new ConnectorObject
        {
            SourceId = id ?? string.Empty,
            ObjectClass = nativeClass,
            Attributes = attrs
        };

    private static bool IsRemoved(IDictionary<string, object>? additional)
    {
        if (additional is null) return false;
        return additional.ContainsKey("@removed");
    }

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrEmpty(s)) return;
        dict[key] = value;
    }

    private async Task<GraphServiceClient> CreateClientAsync()
    {
        var credential = await CreateCredentialAsync();
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    private async Task<ClientSecretCredential> CreateCredentialAsync()
    {
        var creds = await EntraIDCredentialReader.ReadAsync(_protector, _tenantId)
            ?? throw new InvalidOperationException($"No 'entraid' credential stored for tenant {_tenantId}.");
        return new ClientSecretCredential(creds.TenantId, creds.ClientId, creds.ClientSecret);
    }

    private sealed class DeltaLinkHolder
    {
        public string? DeltaLink { get; set; }
    }
}
