using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Entra enterprise-app role-assignment stream (ObjectClass "approleassignment").
/// Pages the tenant's service principals, then for each one pages
/// <c>servicePrincipals/{id}/appRoleAssignedTo</c> and emits one ConnectorObject per
/// assignment — i.e. "who (user/group/SP) has what role on which enterprise app".
/// The SP being enumerated is the RESOURCE (the app granting access);
/// <c>PrincipalId</c> is the assignee.
///
/// SourceId = the appRoleAssignment id (a stable per-assignment key, so re-runs
/// UPDATE/no-op rather than duplicate). Attribute keys are PascalCase to match the
/// IC sink's BuildAppRoleRow LookupAttr calls.
///
/// Least-privilege app-registration scopes: Application.Read.All (service
/// principals) + AppRoleAssignment.ReadWrite.All OR Directory.Read.All
/// (appRoleAssignedTo). A 403 on the SP listing aborts the stream (loud); a 403 on a
/// single SP's assignments warns and skips THAT app, never failing the whole run —
/// same fail-soft contract as the sign-in/usage/license readers.
/// </summary>
internal sealed class EntraAppRoleSource
{
    public const string ObjectClassName = "approleassignment";

    // Only enumerate SPs that actually represent enterprise apps with assignments
    // (skip the noise of every first-party microsoft SP). Bounded by MaxObjects.
    // appRoles is selected so we can resolve each assignment's appRoleId -> friendly
    // name without a second round-trip per SP.
    private static readonly string[] SpSelect = { "id", "displayName", "appId", "servicePrincipalType", "appRoles" };

    // The all-zeros appRoleId Graph uses when an assignment grants the app's
    // implicit "default access" (no specific app role).
    private static readonly Guid DefaultAccessRoleId = Guid.Empty;
    private const string DefaultAccessLabel = "Default Access";

    private readonly GraphServiceClient _client;
    private readonly ILogger _logger;

    public EntraAppRoleSource(GraphServiceClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pageSize = scope.PageSize > 0 ? scope.PageSize : 999;

        ServicePrincipalCollectionResponse? spPage = null;
        if (!await TryAsync(
                () => _client.ServicePrincipals.GetAsync(req =>
                {
                    req.QueryParameters.Top = pageSize;
                    req.QueryParameters.Select = SpSelect;
                }, cancellationToken),
                r => spPage = r, "Application.Read.All", "servicePrincipals"))
            yield break;

        var emitted = 0;
        var capped = false;

        while (spPage?.Value != null)
        {
            foreach (var sp in spPage.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(sp.Id)) continue;

                // Build the appRoleId -> friendly-name map ONCE per resource SP, reused
                // across all of this app's assignments below (the SP carries appRoles
                // because we selected it). Keyed by the role's GUID.
                var roleNames = BuildRoleNameMap(sp);

                // Per-app assignment listing, paged. A 403/404 on a single app skips it.
                AppRoleAssignmentCollectionResponse? aPage = null;
                if (!await TryAsync(
                        () => _client.ServicePrincipals[sp.Id].AppRoleAssignedTo.GetAsync(req =>
                        {
                            req.QueryParameters.Top = 999;
                        }, cancellationToken),
                        r => aPage = r, "Directory.Read.All", $"appRoleAssignedTo({sp.DisplayName})",
                        abortStream: false))
                    continue;

                while (aPage?.Value != null)
                {
                    foreach (var a in aPage.Value)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(a.Id)) continue;
                        if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value)
                        {
                            capped = true;
                            break;
                        }
                        emitted++;
                        yield return Build(a, sp, roleNames);
                    }
                    if (capped) break;
                    if (string.IsNullOrEmpty(aPage.OdataNextLink)) break;
                    aPage = await _client.ServicePrincipals[sp.Id].AppRoleAssignedTo
                        .WithUrl(aPage.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
                }
                if (capped) break;
            }

            if (capped) break;
            if (string.IsNullOrEmpty(spPage.OdataNextLink)) break;
            spPage = await _client.ServicePrincipals.WithUrl(spPage.OdataNextLink)
                .GetAsync(cancellationToken: cancellationToken);
        }

        if (capped)
            _logger.LogWarning(
                "EntraID approleassignment: read CAPPED after {Emitted} assignment(s) (maxObjects={Max}). Some assignments were not emitted this run.",
                emitted, scope.MaxObjects!.Value);
    }

    private static ConnectorObject Build(
        Microsoft.Graph.Models.AppRoleAssignment a, ServicePrincipal resourceSp,
        IReadOnlyDictionary<Guid, string> roleNames)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ObjectClassName,
            ["AppRoleAssignmentId"] = a.Id,
        };
        Set(attrs, "PrincipalId", a.PrincipalId?.ToString());
        Set(attrs, "PrincipalType", a.PrincipalType);
        Set(attrs, "PrincipalDisplayName", a.PrincipalDisplayName);
        // The resource is the SP we are enumerating; a.ResourceId echoes it.
        Set(attrs, "ResourceId", (a.ResourceId ?? Guid.Empty) != Guid.Empty ? a.ResourceId?.ToString() : resourceSp.Id);
        Set(attrs, "ResourceDisplayName", a.ResourceDisplayName ?? resourceSp.DisplayName);
        Set(attrs, "AppRoleId", a.AppRoleId?.ToString());
        // Friendly name resolved from the resource SP's appRoles; never null (falls
        // back to "Default Access" for the all-zeros default-access grant or an
        // appRoleId not present in this app's role catalog).
        Set(attrs, "AppRoleName", ResolveRoleName(a.AppRoleId, roleNames));
        Set(attrs, "CreatedDateTime", a.CreatedDateTime?.ToString("o"));

        return new ConnectorObject
        {
            SourceId = a.Id ?? string.Empty,
            ObjectClass = ObjectClassName,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Project the resource SP's appRoles collection into a GUID -> friendly-name map.
    /// Prefers the role's DisplayName, falls back to its Value (the programmatic claim
    /// string), then the GUID itself. Returns an empty map when the SP exposes no roles
    /// (e.g. a default-access-only app) — resolution then falls back per assignment.
    /// </summary>
    private static IReadOnlyDictionary<Guid, string> BuildRoleNameMap(ServicePrincipal sp)
    {
        var map = new Dictionary<Guid, string>();
        if (sp.AppRoles == null) return map;

        foreach (var role in sp.AppRoles)
        {
            if (!role.Id.HasValue || role.Id.Value == DefaultAccessRoleId) continue;
            var name = !string.IsNullOrWhiteSpace(role.DisplayName) ? role.DisplayName
                : !string.IsNullOrWhiteSpace(role.Value) ? role.Value
                : role.Id.Value.ToString();
            map[role.Id.Value] = name!;
        }
        return map;
    }

    private static string ResolveRoleName(Guid? appRoleId, IReadOnlyDictionary<Guid, string> roleNames)
    {
        if (!appRoleId.HasValue || appRoleId.Value == DefaultAccessRoleId)
            return DefaultAccessLabel;
        return roleNames.TryGetValue(appRoleId.Value, out var name) ? name : DefaultAccessLabel;
    }

    /// <summary>
    /// Run a Graph call and assign its result. Returns true to continue. A 403/404 is
    /// logged at Warning naming the scope; if <paramref name="abortStream"/> the whole
    /// stream yields nothing (return false to the caller's yield break), otherwise the
    /// caller skips just this item (also false, but caller continues). Other errors
    /// propagate. Never logs token/secret material.
    /// </summary>
    private async Task<bool> TryAsync<T>(
        Func<Task<T?>> fetch, Action<T?> assign, string scope, string what, bool abortStream = true)
        where T : class
    {
        try
        {
            assign(await fetch());
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbiddenOrMissing(ex))
        {
            _logger.LogWarning(
                "EntraID: {Disposition} class {ObjectClass} ({What}) — needs scope {Scope} (HTTP {Status})",
                abortStream ? "aborting" : "skipping", ObjectClassName, what, scope, ex.ResponseStatusCode);
            return false;
        }
    }

    private static bool IsForbiddenOrMissing(Microsoft.Graph.Models.ODataErrors.ODataError ex)
    {
        if (ex.ResponseStatusCode is 403 or 404) return true;
        var code = ex.Error?.Code;
        return string.Equals(code, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string str && string.IsNullOrEmpty(str)) return;
        dict[key] = value;
    }
}
