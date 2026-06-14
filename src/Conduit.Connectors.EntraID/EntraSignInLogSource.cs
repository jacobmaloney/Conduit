using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Entra sign-in EVENT stream (ObjectClass "signinlog"). Pages
/// <c>/auditLogs/signIns</c> via the Graph SDK over a recent window (default last
/// 30 days — the Entra ID P1 retention floor) and emits one ConnectorObject per
/// sign-in. SourceId = the sign-in's own id (a stable per-event key, so re-runs
/// UPDATE rather than duplicate). Attributes carry the full SignInLogEvent field
/// set keyed by camelCase names matching IC's ingest DTO.
///
/// Least-privilege app-registration scope: AuditLog.Read.All. A 403 (scope not
/// consented) is logged at Warning naming the scope and yields NOTHING rather than
/// failing the whole run — same fail-soft contract as the directory enumerators'
/// <c>TryFirstPageAsync</c>.
///
/// High-volume guard: sign-ins are unbounded on a busy tenant, so paging stops at
/// a configurable max-pages / max-events ceiling (scope.MaxObjects + an internal
/// page cap) and logs when it caps. Field shapes mirror IC's GraphQueryService:
/// Status = errorCode == 0 ? "Success" : "Failure"; DeviceDetail / Location are
/// JSON strings; IsInteractive defaults to false.
/// </summary>
internal sealed class EntraSignInLogSource
{
    public const string ObjectClassName = "signinlog";

    // Default look-back when the scope carries no explicit window.
    private const int DefaultWindowDays = 30;
    // Hard ceiling on pages followed so a runaway tenant can't OOM the run. 999
    // events/page * 1000 pages ≈ 1M events before the guard trips.
    private const int MaxPages = 1000;

    private readonly GraphServiceClient _client;
    private readonly ILogger _logger;

    public EntraSignInLogSource(GraphServiceClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var windowDays = ResolveWindowDays(scope);
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var iso = since.ToString("yyyy-MM-ddTHH:mm:ssZ");

        SignInCollectionResponse? page = null;
        if (!await TryFirstPageAsync(
            () => _client.AuditLogs.SignIns.GetAsync(req =>
            {
                req.QueryParameters.Filter = $"createdDateTime ge {iso}";
                req.QueryParameters.Top = 999;
                req.QueryParameters.Orderby = new[] { "createdDateTime desc" };
            }, cancellationToken),
            r => page = r))
            yield break;

        var emitted = 0;
        var pages = 0;
        var capped = false;

        while (page?.Value != null)
        {
            foreach (var s in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value)
                {
                    capped = true;
                    break;
                }
                if (string.IsNullOrEmpty(s.Id)) continue;
                emitted++;
                yield return Convert(s);
            }

            if (capped) break;
            if (string.IsNullOrEmpty(page.OdataNextLink)) break;
            if (++pages >= MaxPages)
            {
                capped = true;
                break;
            }
            page = await _client.AuditLogs.SignIns.WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: cancellationToken);
        }

        if (capped)
        {
            _logger.LogWarning(
                "EntraID signinlog: read CAPPED after {Emitted} event(s) (window={Days}d, maxObjects={Max}, maxPages={Pages}). " +
                "Some sign-ins were not emitted this run.",
                emitted, windowDays,
                scope.MaxObjects.HasValue ? scope.MaxObjects.Value.ToString() : "none", MaxPages);
        }
    }

    /// <summary>
    /// Optional per-scope window override. <c>QueryExpression</c> carrying a plain
    /// integer is read as a day count; anything else falls back to the 30-day
    /// default. Negative / zero values are ignored.
    /// </summary>
    private static int ResolveWindowDays(SyncProjectScope scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.QueryExpression)
            && int.TryParse(scope.QueryExpression.Trim(), out var days)
            && days > 0)
            return days;
        return DefaultWindowDays;
    }

    private static ConnectorObject Convert(SignIn s)
    {
        var errorCode = s.Status?.ErrorCode;
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ObjectClassName,
            ["signInId"] = s.Id,
            ["isInteractive"] = s.IsInteractive ?? false
        };

        Set(attrs, "userSourceUniqueId", s.UserId);
        Set(attrs, "userPrincipalName", s.UserPrincipalName);
        Set(attrs, "signInDateTime", s.CreatedDateTime?.ToString("o"));
        Set(attrs, "appDisplayName", s.AppDisplayName);
        Set(attrs, "appId", s.AppId);
        Set(attrs, "clientAppUsed", s.ClientAppUsed);
        Set(attrs, "deviceDetail", JsonSerializer.Serialize(s.DeviceDetail));
        Set(attrs, "ipAddress", s.IpAddress);
        Set(attrs, "location", JsonSerializer.Serialize(s.Location));
        attrs["status"] = errorCode == 0 ? "Success" : "Failure";
        if (errorCode.HasValue) attrs["errorCode"] = errorCode.Value;
        Set(attrs, "riskLevel", s.RiskLevelDuringSignIn?.ToString());
        Set(attrs, "riskState", s.RiskState?.ToString());
        Set(attrs, "conditionalAccessStatus", s.ConditionalAccessStatus?.ToString());
        Set(attrs, "resourceDisplayName", s.ResourceDisplayName);
        Set(attrs, "resourceId", s.ResourceId);

        return new ConnectorObject
        {
            SourceId = s.Id ?? string.Empty,
            ObjectClass = ObjectClassName,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Runs the first-page fetch and assigns via <paramref name="assign"/>. Returns
    /// true to continue paging. A 403 (app registration lacks AuditLog.Read.All) is
    /// logged at Warning naming the scope and returns false (yield nothing); any
    /// other error propagates. Never logs token/secret material. Mirrors
    /// <c>EntraIDSource.TryFirstPageAsync</c>.
    /// </summary>
    private async Task<bool> TryFirstPageAsync(
        Func<Task<SignInCollectionResponse?>> fetch, Action<SignInCollectionResponse?> assign)
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
                ObjectClassName, "AuditLog.Read.All");
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
