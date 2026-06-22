using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// Entra license-assignment stream (ObjectClass "license"). Joins the org-level SKU
/// inventory (<c>/subscribedSkus</c>) with each user's <c>assignedLicenses</c>
/// (<c>/users?$select=id,userPrincipalName,assignedLicenses</c>) and emits ONE
/// ConnectorObject per (user, SKU) pair. Each emitted object carries BOTH the pool
/// fields (SkuId/SkuName/part number + prepaidUnits capacity counts, identical
/// across every row of a SKU) and the assignee fields (UPN + objectGUID), so IC's
/// /api/objects/licenses/bulk can upsert the LicensePools inventory and the per-user
/// LicenseAssignments from the same row set.
///
/// SourceId = "{userId}:{skuId}" — a stable per-assignment key so re-runs UPDATE
/// rather than duplicate. Attribute keys are PascalCase to match the IC sink's
/// BuildLicenseRow LookupAttr calls.
///
/// Least-privilege app-registration scopes: Organization.Read.All (subscribedSkus)
/// + User.Read.All (assignedLicenses). A 403 on either call is logged at Warning
/// naming the scope and yields NOTHING rather than failing the whole run — same
/// fail-soft contract as the sign-in + usage readers.
/// </summary>
internal sealed class EntraLicenseSource
{
    public const string ObjectClassName = "license";

    private readonly GraphServiceClient _client;
    private readonly ILogger _logger;

    public EntraLicenseSource(GraphServiceClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1) Pool inventory: skuId -> (name, partNumber, capacity). Without it we
        //    cannot describe the pools, so a 403 here aborts the whole license stream.
        SubscribedSkuCollectionResponse? skus = null;
        if (!await TryAsync(
                () => _client.SubscribedSkus.GetAsync(cancellationToken: cancellationToken),
                r => skus = r, "Organization.Read.All", "subscribedSkus"))
            yield break;

        var pools = new Dictionary<string, PoolInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in skus?.Value ?? new List<SubscribedSku>())
        {
            var skuId = s.SkuId?.ToString();
            if (string.IsNullOrEmpty(skuId)) continue;
            pools[skuId] = new PoolInfo(
                SkuName: s.SkuPartNumber ?? skuId,
                SkuPartNumber: s.SkuPartNumber,
                TotalUnits: s.PrepaidUnits?.Enabled ?? 0,
                ConsumedUnits: s.ConsumedUnits ?? 0,
                WarningUnits: s.PrepaidUnits?.Warning ?? 0,
                SuspendedUnits: s.PrepaidUnits?.Suspended ?? 0);
        }

        if (pools.Count == 0)
        {
            _logger.LogInformation("EntraID license: tenant advertises no subscribed SKUs — nothing to sync.");
            yield break;
        }

        // 2) Per-user assignedLicenses, paged. A 403 here aborts (no assignments to emit).
        UserCollectionResponse? page = null;
        if (!await TryAsync(
                () => _client.Users.GetAsync(req =>
                {
                    req.QueryParameters.Select = new[] { "id", "userPrincipalName", "assignedLicenses" };
                    req.QueryParameters.Top = 999;
                }, cancellationToken),
                r => page = r, "User.Read.All", "users"))
            yield break;

        var emitted = 0;
        var capped = false;

        while (page?.Value != null)
        {
            foreach (var u in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (u.AssignedLicenses is null || u.AssignedLicenses.Count == 0) continue;

                foreach (var lic in u.AssignedLicenses)
                {
                    var skuId = lic.SkuId?.ToString();
                    if (string.IsNullOrEmpty(skuId) || !pools.TryGetValue(skuId, out var pool)) continue;

                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value)
                    {
                        capped = true;
                        break;
                    }
                    emitted++;
                    yield return Build(u, skuId, pool);
                }
                if (capped) break;
            }

            if (capped) break;
            if (string.IsNullOrEmpty(page.OdataNextLink)) break;
            page = await _client.Users.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }

        if (capped)
            _logger.LogWarning(
                "EntraID license: read CAPPED after {Emitted} assignment(s) (maxObjects={Max}). Some assignments were not emitted this run.",
                emitted, scope.MaxObjects!.Value);
    }

    private readonly record struct PoolInfo(
        string SkuName, string? SkuPartNumber,
        int TotalUnits, int ConsumedUnits, int WarningUnits, int SuspendedUnits);

    private static ConnectorObject Build(User u, string skuId, PoolInfo pool)
    {
        var userId = u.Id ?? string.Empty;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ObjectClassName,
            ["SkuId"] = skuId,
            ["SkuName"] = pool.SkuName,
            ["TotalUnits"] = pool.TotalUnits.ToString(CultureInfo.InvariantCulture),
            ["ConsumedUnits"] = pool.ConsumedUnits.ToString(CultureInfo.InvariantCulture),
            ["WarningUnits"] = pool.WarningUnits.ToString(CultureInfo.InvariantCulture),
            ["SuspendedUnits"] = pool.SuspendedUnits.ToString(CultureInfo.InvariantCulture),
            ["UserSourceUniqueId"] = userId,
            // Entra licensing is direct OR group-inherited; Graph's assignedLicenses
            // doesn't itself say which, so we report "Direct" by default. (Group-based
            // licensing detail would come from licenseAssignmentStates — a future pass.)
            ["AssignmentSource"] = "Direct",
        };
        Set(attrs, "SkuPartNumber", pool.SkuPartNumber);
        Set(attrs, "UserPrincipalName", u.UserPrincipalName);

        return new ConnectorObject
        {
            // Stable per-assignment key: a user can hold many SKUs, so key on both.
            SourceId = string.Concat(userId, ":", skuId),
            ObjectClass = ObjectClassName,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Run a Graph call and assign its result. Returns true to continue. A 403 (the
    /// app registration lacks <paramref name="scope"/>) is logged at Warning naming the
    /// scope and returns false (yield nothing); any other error propagates. Never logs
    /// token/secret material. Mirrors EntraSignInLogSource.TryFirstPageAsync.
    /// </summary>
    private async Task<bool> TryAsync<T>(Func<Task<T?>> fetch, Action<T?> assign, string scope, string what)
        where T : class
    {
        try
        {
            assign(await fetch());
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
        {
            _logger.LogWarning(
                "EntraID: skipping class {ObjectClass} ({What}) — app registration lacks scope {Scope} (403)",
                ObjectClassName, what, scope);
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

    private static void Set(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is null) return;
        if (value is string str && string.IsNullOrEmpty(str)) return;
        dict[key] = value;
    }
}
