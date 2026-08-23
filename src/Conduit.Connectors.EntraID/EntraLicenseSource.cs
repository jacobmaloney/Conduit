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
/// + User.Read.All (assignedLicenses + licenseAssignmentStates). A 403 on
/// /subscribedSkus THROWS (naming the missing scope) so the run goes non-green —
/// without the SKU inventory the whole license stream is silently empty, and a
/// green run over an empty read is exactly the fake-pass this class must not
/// produce. The users call keeps the fail-soft Warning contract of the sign-in +
/// usage readers.
///
/// AssignmentSource per (user, SKU): the user's licenseAssignmentStates entry for
/// that SKU says whether the license came direct or via group-based licensing
/// (assignedByGroup) — "Direct" / "Group", or "Unknown" when Graph returned no
/// states collection (or no entry for that SKU).
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
        //    cannot describe the pools — required: a 403 here THROWS so the run
        //    fails visibly instead of yielding nothing and reporting green.
        SubscribedSkuCollectionResponse? skus = null;
        await TryAsync(
            () => _client.SubscribedSkus.GetAsync(cancellationToken: cancellationToken),
            r => skus = r, "Organization.Read.All", "subscribedSkus", required: true);

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
                    req.QueryParameters.Select = new[] { "id", "userPrincipalName", "assignedLicenses", "licenseAssignmentStates" };
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
            ["AssignmentSource"] = DeriveAssignmentSource(u.LicenseAssignmentStates, skuId),
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
    /// Group-based vs direct licensing, from the user's licenseAssignmentStates. A
    /// state entry whose assignedByGroup is non-null came through group-based
    /// licensing; a null assignedByGroup is a direct grant (which wins when both
    /// exist for one SKU, because a direct grant is what an operator can act on).
    /// No evidence — Graph omitted the states collection, or it carries no entry
    /// for this SKU — means we do not know: "Unknown", never a fabricated
    /// "Direct". Internal-static for the unit tests.
    /// </summary>
    internal static string DeriveAssignmentSource(List<LicenseAssignmentState>? states, string skuId)
    {
        if (states is null) return "Unknown";

        var matched = false;
        foreach (var s in states)
        {
            if (!string.Equals(s.SkuId?.ToString(), skuId, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.AssignedByGroup is null) return "Direct";
            matched = true;
        }
        return matched ? "Group" : "Unknown";
    }

    /// <summary>
    /// Run a Graph call and assign its result. Returns true to continue. A 403 (the
    /// app registration lacks <paramref name="scope"/>) is logged at Warning naming the
    /// scope and returns false (yield nothing) — unless <paramref name="required"/>,
    /// where the 403 is rethrown wrapped in a message naming the missing scope so the
    /// orchestrator marks the step (and the run) failed instead of green-empty. Any
    /// other error propagates. Never logs token/secret material. Mirrors
    /// EntraSignInLogSource.TryFirstPageAsync.
    /// </summary>
    private async Task<bool> TryAsync<T>(Func<Task<T?>> fetch, Action<T?> assign, string scope, string what, bool required = false)
        where T : class
    {
        try
        {
            assign(await fetch());
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (IsForbidden(ex))
        {
            if (required)
                throw new InvalidOperationException(
                    $"EntraID license read failed: the app registration lacks Graph scope {scope} (403 on {what}). " +
                    $"Grant {scope} (application) and admin-consent it, then re-run.", ex);

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
