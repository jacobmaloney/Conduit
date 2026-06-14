using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Per-user Microsoft 365 usage stream (ObjectClass "m365usage"). Joins five
/// Graph usage reports by UserPrincipalName into one ConnectorObject per user:
///
///   getOffice365ActiveUserDetail   — the SPINE: license flags + per-workload
///                                     last-activity dates + UPN/displayName.
///   getOneDriveUsageAccountDetail  — OneDrive storage used/allocated.
///   getMailboxUsageDetail          — mailbox storage used/quota.
///   getM365AppUserDetail           — per-app (Outlook/Word/Excel/…) activity.
///   getTeamsUserActivityUserDetail — Teams chat/call/meeting counts.
///
/// Least-privilege app-registration scope: Reports.Read.All (application
/// permission). A per-report 403 warns and skips THAT report; a 403 on the
/// spine report yields nothing with a loud warning naming the scope.
///
/// SourceId = UPN (the stable join key to the IC user object). Native object
/// class emitted lowercase in attrs["objectClass"] = "m365usage".
///
/// ── THE ANONYMIZATION GATE ──────────────────────────────────────────────────
/// Since June 2023 Microsoft 365 usage reports are anonymized by default
/// (admin/reportSettings.displayConcealedNames = true): UPN and displayName come
/// back as opaque hash tokens with no '@domain', which breaks every per-user
/// join. This source DETECTS concealment (see <see cref="LooksConcealed"/>) and,
/// when detected, emits NOTHING and logs a loud LogError telling the admin to
/// disable concealment tenant-wide:
///   PATCH https://graph.microsoft.com/v1.0/admin/reportSettings
///         { "displayConcealedNames": false }
/// (the change is audited in Purview). We never emit concealed data.
/// </summary>
internal sealed class M365UsageReportSource
{
    public const string ObjectClassName = "m365usage";
    private const string Period = "D30";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly ClientSecretCredential _credential;
    private readonly ILogger _logger;

    public M365UsageReportSource(ClientSecretCredential credential, ILogger logger)
    {
        _credential = credential;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var token = await AcquireTokenAsync(cancellationToken);
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // SPINE — without it there is nothing to join onto. A 403 here aborts the
        // whole m365usage stream (loudly); a 403 on any later report only drops
        // that report's columns.
        var activeUsers = await FetchReportAsync(http, "getOffice365ActiveUserDetail", "Reports.Read.All", cancellationToken);
        if (activeUsers is null)
        {
            _logger.LogError(
                "EntraID m365usage: spine report getOffice365ActiveUserDetail returned 403 — app registration lacks scope Reports.Read.All. Emitting nothing.");
            yield break;
        }

        // ── Anonymization gate (run-level) ──
        // The spine returned rows but produced no usable (non-blank) UPN sample:
        // that is the shape concealment takes when Graph blanks/relocates the
        // identifier, so FAIL CLOSED rather than fall through and risk emitting
        // concealed rows.
        var upnSample = activeUsers
            .Select(r => GetField(r, "userPrincipalName"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(50)
            .ToList();
        if (activeUsers.Count > 0 && upnSample.Count == 0)
        {
            _logger.LogError(
                "EntraID m365usage: the spine report returned {RowCount} rows but no usable UserPrincipalName — " +
                "treating as ANONYMIZED and emitting nothing. An admin must run: " +
                "PATCH https://graph.microsoft.com/v1.0/admin/reportSettings {{\"displayConcealedNames\": false}} (audited in Purview).",
                activeUsers.Count);
            yield break;
        }
        if (LooksConcealed(upnSample))
        {
            _logger.LogError(
                "EntraID m365usage: usage reports are ANONYMIZED (admin/reportSettings.displayConcealedNames=true). " +
                "Per-user joins are impossible with concealed identifiers, so NOTHING will be emitted. To enable, an admin must run: " +
                "PATCH https://graph.microsoft.com/v1.0/admin/reportSettings {{\"displayConcealedNames\": false}} (audited in Purview).");
            yield break;
        }

        var oneDrive = await FetchByUpnAsync(http, "getOneDriveUsageAccountDetail", cancellationToken);
        var mailbox = await FetchByUpnAsync(http, "getMailboxUsageDetail", cancellationToken);
        var apps = await FetchByUpnAsync(http, "getM365AppUserDetail", cancellationToken);
        var teams = await FetchByUpnAsync(http, "getTeamsUserActivityUserDetail", cancellationToken);

        var emitted = 0;
        var concealedSkipped = 0;
        foreach (var row in activeUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value) yield break;

            var rawUpn = GetField(row, "userPrincipalName");
            if (string.IsNullOrWhiteSpace(rawUpn)) continue;
            var upn = rawUpn!.Trim();

            // Per-row gate: the run-level vote passed, but never emit an individual
            // row whose identifier is concealed (mixed/transitional concealment).
            if (IsConcealedValue(upn))
            {
                concealedSkipped++;
                continue;
            }

            var obj = BuildUsageObject(
                upn,
                row,
                Lookup(oneDrive, upn),
                Lookup(mailbox, upn),
                Lookup(apps, upn),
                Lookup(teams, upn));

            emitted++;
            yield return obj;
        }

        if (concealedSkipped > 0)
        {
            _logger.LogWarning(
                "EntraID m365usage: skipped {Skipped} row(s) with concealed identifiers (partial/transitional anonymization); emitted {Emitted}.",
                concealedSkipped, emitted);
        }
    }

    /// <summary>
    /// Merges one spine row plus the matching per-report rows into a single
    /// ConnectorObject. Exposed (internal) so the mapping is unit-testable with
    /// parsed JSON rows and no live Graph. Any report dictionary may be null when
    /// that report 403'd — its columns are simply omitted.
    /// </summary>
    internal static ConnectorObject BuildUsageObject(
        string upn,
        IReadOnlyDictionary<string, JsonElement> active,
        IReadOnlyDictionary<string, JsonElement>? oneDrive,
        IReadOnlyDictionary<string, JsonElement>? mailbox,
        IReadOnlyDictionary<string, JsonElement>? apps,
        IReadOnlyDictionary<string, JsonElement>? teams)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ObjectClassName,
            ["id"] = upn,
            ["UserPrincipalName"] = upn
        };

        // ── spine: getOffice365ActiveUserDetail ──
        SetStr(attrs, "DisplayName", GetField(active, "displayName"));
        SetBool(attrs, "HasExchangeLicense", GetField(active, "hasExchangeLicense"));
        SetBool(attrs, "HasOneDriveLicense", GetField(active, "hasOneDriveLicense"));
        SetBool(attrs, "HasSharePointLicense", GetField(active, "hasSharePointLicense"));
        SetBool(attrs, "HasTeamsLicense", GetField(active, "hasTeamsLicense"));
        SetBool(attrs, "HasYammerLicense", GetField(active, "hasYammerLicense"));
        SetDate(attrs, "ExchangeLastActivityDate", GetField(active, "exchangeLastActivityDate"));
        SetDate(attrs, "OneDriveLastActivityDate", GetField(active, "oneDriveLastActivityDate"));
        SetDate(attrs, "SharePointLastActivityDate", GetField(active, "sharePointLastActivityDate"));
        SetDate(attrs, "TeamsLastActivityDate", GetField(active, "teamsLastActivityDate"));
        SetDate(attrs, "YammerLastActivityDate", GetField(active, "yammerLastActivityDate"));
        SetStr(attrs, "AssignedProducts", GetField(active, "assignedProducts"));
        SetDate(attrs, "ReportRefreshDate", GetField(active, "reportRefreshDate"));

        // ── getOneDriveUsageAccountDetail ──
        if (oneDrive is not null)
        {
            SetLong(attrs, "OneDriveStorageUsedBytes", GetField(oneDrive, "storageUsedInBytes"));
            SetLong(attrs, "OneDriveStorageAllocatedBytes", GetField(oneDrive, "storageAllocatedInBytes"));
        }

        // ── getMailboxUsageDetail ──
        if (mailbox is not null)
        {
            SetLong(attrs, "MailboxStorageUsedBytes", GetField(mailbox, "storageUsedInBytes"));
            SetLong(attrs, "MailboxQuotaBytes", GetField(mailbox, "prohibitSendReceiveQuotaInBytes"));
        }

        // ── getM365AppUserDetail ──
        if (apps is not null)
        {
            SetDate(attrs, "M365AppLastActivityDate", GetField(apps, "lastActivityDate"));
        }

        // ── getTeamsUserActivityUserDetail ──
        if (teams is not null)
        {
            SetLong(attrs, "TeamsChatMessages", GetField(teams, "teamChatMessageCount"));
            SetLong(attrs, "TeamsPrivateChatMessages", GetField(teams, "privateChatMessageCount"));
            SetLong(attrs, "TeamsCallCount", GetField(teams, "callCount"));
            SetLong(attrs, "TeamsMeetingCount", GetField(teams, "meetingCount"));
        }

        return new ConnectorObject
        {
            SourceId = upn,
            ObjectClass = ObjectClassName,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Heuristic concealment detector. A usage-report UPN value is "concealed"
    /// when it is NOT a real principal name — it has no '@' AND looks like an
    /// opaque hash token (long, hex/base64-ish, no dotted TLD). The stream is
    /// treated as concealed when a strong majority (≥80%) of non-empty sampled
    /// UPNs look concealed. Real UPNs like "alice@contoso.com" never match.
    ///
    /// Static + dependency-free so it is directly unit-testable.
    /// </summary>
    public static bool LooksConcealed(IEnumerable<string?> upnSample)
    {
        var values = upnSample.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (values.Count == 0) return false;

        var concealed = values.Count(IsConcealedValue);
        return concealed * 100 >= values.Count * 80;
    }

    private static bool IsConcealedValue(string value)
    {
        // A genuine UPN contains '@' and a dotted domain — never concealed.
        if (value.Contains('@')) return false;

        // Opaque token shape: reasonably long and composed only of token-ish
        // characters (hex / base64 alphabet, dashes, underscores) with no '.'+TLD.
        if (value.Length < 16) return false;
        foreach (var ch in value)
        {
            var ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                     (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '=' || ch == '+' || ch == '/';
            if (!ok) return false;
        }
        return true;
    }

    // ─── report fetch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a usage report as JSON (period D30) and returns its rows. Pages via
    /// @odata.nextLink. Returns null on a 403 (caller decides whether that's fatal).
    /// Any other non-success status throws.
    /// </summary>
    private async Task<List<Dictionary<string, JsonElement>>?> FetchReportAsync(
        HttpClient http, string reportFunction, string scopeHint, CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, JsonElement>>();
        var url = $"{GraphBase}/reports/{reportFunction}(period='{Period}')?$format=application/json";

        while (!string.IsNullOrEmpty(url))
        {
            using var resp = await http.GetAsync(url, cancellationToken);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "EntraID m365usage: skipping report {Report} — app registration lacks scope {Scope} (403)",
                    reportFunction, scopeHint);
                return null;
            }
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in el.EnumerateObject())
                        row[prop.Name] = prop.Value.Clone();
                    rows.Add(row);
                }
            }

            var nextLink = root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (!string.IsNullOrEmpty(nextLink) && !IsGraphHost(nextLink!))
            {
                _logger.LogWarning(
                    "EntraID m365usage: refusing to follow non-Graph nextLink host {Host} on report {Report}; stopping paging.",
                    SafeHost(nextLink!), reportFunction);
                break;
            }
            url = nextLink;
        }

        return rows;
    }

    /// <summary>
    /// True only when <paramref name="url"/> is an absolute HTTPS URL whose host is
    /// graph.microsoft.com (or a subdomain ending in ".graph.microsoft.com"). Guards
    /// the @odata.nextLink follow so a tampered/off-host nextLink can never receive
    /// the bearer token carried on HttpClient.DefaultRequestHeaders. Static +
    /// dependency-free so it is directly unit-testable.
    /// </summary>
    public static bool IsGraphHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.Host;
        return string.Equals(host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".graph.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host for log output only — never the full URL (it can carry tokens).</summary>
    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unparseable)";

    /// <summary>Fetch a non-spine report and index its rows by userPrincipalName. Null on 403.</summary>
    private async Task<Dictionary<string, Dictionary<string, JsonElement>>?> FetchByUpnAsync(
        HttpClient http, string reportFunction, CancellationToken cancellationToken)
    {
        var rows = await FetchReportAsync(http, reportFunction, "Reports.Read.All", cancellationToken);
        if (rows is null) return null;

        var byUpn = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var upn = GetField(row, "userPrincipalName");
            if (!string.IsNullOrWhiteSpace(upn) && !byUpn.ContainsKey(upn!))
                byUpn[upn!] = row;
        }
        return byUpn;
    }

    private static IReadOnlyDictionary<string, JsonElement>? Lookup(
        Dictionary<string, Dictionary<string, JsonElement>>? index, string upn) =>
        index is not null && index.TryGetValue(upn, out var row) ? row : null;

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        var ctx = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
        var token = await _credential.GetTokenAsync(ctx, cancellationToken);
        return token.Token;
    }

    // ─── field helpers ──────────────────────────────────────────────────────────

    private static string? GetField(IReadOnlyDictionary<string, JsonElement> row, string name) =>
        row.TryGetValue(name, out var el) && el.ValueKind != JsonValueKind.Null
            ? (el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText())
            : null;

    private static void SetStr(Dictionary<string, object?> attrs, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) attrs[key] = value;
    }

    private static void SetBool(Dictionary<string, object?> attrs, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (bool.TryParse(value, out var b)) attrs[key] = b;
    }

    private static void SetLong(Dictionary<string, object?> attrs, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) attrs[key] = n;
    }

    private static void SetDate(Dictionary<string, object?> attrs, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        // Graph usage reports emit dates as "yyyy-MM-dd"; pass through ISO form.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            attrs[key] = dt.ToString("o");
        else
            attrs[key] = value;
    }
}
