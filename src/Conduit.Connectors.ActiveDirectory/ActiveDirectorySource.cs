using System;
using System.Collections.Generic;
using System.Linq;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Readers.ActiveDirectory;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// AD source via System.DirectoryServices.Protocols. Uses paged LDAP search
/// (LDAP paged-results control RFC 2696) so large directories don't materialize
/// at once. Tenant connection details (host + port + base DN) come from
/// Tenants.Domain (host:port form, ":389" default) and the per-project scope's
/// BaseDN. Credentials come from ConnectionCredentials (CredentialName="ldap"),
/// stored as JSON {"Username":"...","Password":"..."}.
///
/// The host owns the authenticated bind; the shared reader pages one
/// objectClass at a time, all attributes returned dropped into ConnectorObject.
/// </summary>
public sealed class ActiveDirectorySource : IConnectorSource
{
    private readonly Guid _tenantId;
    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;
    private readonly ILogger<ActiveDirectorySource> _logger;

    /// <summary>
    /// Structural attributes the connector ITSELF depends on regardless of which
    /// attributes a step maps. These are ALWAYS unioned into the requested set so a
    /// trimmed projection can never starve SourceId resolution (objectGUID), the
    /// incremental cursor (whenChanged), class/category gating, or the common
    /// identity keys the orchestrator/sinks rely on. distinguishedName is returned
    /// implicitly by every entry (SearchResultEntry.DistinguishedName) so it need
    /// not be listed. If a mapping references an attribute NOT in the step's mapped
    /// set AND not here, that attribute would come back null — by design the mapped
    /// set (plumbed from the orchestrator) covers exactly the mapped attributes.
    /// </summary>
    private static readonly string[] StructuralAttributes =
    {
        "objectGUID",      // primary SourceId
        "objectClass",
        "objectCategory",
        "whenChanged",     // incremental cursor / watermark
        "sAMAccountName",
        "userPrincipalName",
        "userAccountControl",
    };

    public ActiveDirectorySource(
        Guid tenantId,
        TenantRepository tenantRepo,
        CredentialProtector protector,
        ILogger<ActiveDirectorySource> logger)
    {
        _tenantId = tenantId;
        _tenantRepo = tenantRepo;
        _protector = protector;
        _logger = logger;
    }

    public IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        CancellationToken cancellationToken)
        => EnumerateInternalAsync(objectClass, scope, null, new AdWatermark(), null, cancellationToken);

    /// <summary>
    /// Phase 2 incremental: wraps the project's LDAP filter with
    /// <c>(whenChanged&gt;=&lt;generalizedTime&gt;)</c>. Tracks the max whenChanged
    /// seen during enumeration and returns it as the new cursor token (ISO 8601).
    ///
    /// Phase 3: opportunistic tombstone detection. When AD Recycle Bin is enabled
    /// and the bind account has rights to the Deleted Objects container, the
    /// incremental path issues a SECOND search scoped to that container with the
    /// Show-Deleted LDAP control (1.2.840.113556.1.4.417) and emits ConnectorObjects
    /// with attribute <c>_deleted=true</c> for any tombstone whose <c>whenChanged</c>
    /// falls after the cursor. The downstream sink interprets the marker as a
    /// delete. When Recycle Bin isn't enabled the search returns no results and
    /// we log+continue (non-fatal).
    /// </summary>
    public Task<SyncEnumerationResult> EnumerateAsync(
        string objectClass,
        SyncProjectScope scope,
        SyncCursor? cursor,
        CancellationToken cancellationToken)
    {
        var watermark = new AdWatermark();
        var isIncremental = cursor is not null && !string.IsNullOrWhiteSpace(cursor.Token);
        // Complete-read sentinel (Phase 2.2 tombstones). Starts FALSE. The primary
        // live-object enumerator flips it to true ONLY when it falls off the natural
        // end of paging (empty LDAP cookie) with no exception, no cancellation, and
        // no MaxObjects truncation. Any other exit path leaves it false, so the
        // orchestrator will not compute a delete-delta against a partial read.
        var completion = new ReadCompletion();
        var stream = EnumerateWithTombstonesAsync(objectClass, scope, cursor?.Token, watermark, isIncremental, completion, cancellationToken);
        return Task.FromResult(new SyncEnumerationResult
        {
            Objects = stream,
            ResolveNewCursor = () => new SyncCursor
            {
                Token = watermark.IsoSafeOrNow(),
                IssuedAt = DateTime.UtcNow
            },
            IsIncremental = isIncremental,
            WasCompleteRead = () => completion.IsComplete
        });
    }

    /// <summary>
    /// Mutable completion flag threaded through the live-object enumerator. Defaults
    /// to FALSE; only the natural empty-cookie terminus of the primary search loop
    /// sets it true. A throw, a cancellation, or a MaxObjects truncation yield-break
    /// never reaches the set-site, so the flag faithfully reports "this was a clean,
    /// full drain of the source" — the precondition for emitting tombstones.
    /// </summary>
    private sealed class ReadCompletion
    {
        public bool IsComplete { get; set; }
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateWithTombstonesAsync(
        string objectClass,
        SyncProjectScope scope,
        string? sinceIsoUtc,
        AdWatermark watermark,
        bool isIncremental,
        ReadCompletion completion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Primary live-object enumeration. EnumerateInternalAsync owns the
        // completion flag: it sets completion.IsComplete = true ONLY when its paged
        // search drains to the natural empty-cookie terminus. If it throws or the
        // consumer stops early, the flag stays false and no tombstones are computed.
        await foreach (var obj in EnumerateInternalAsync(objectClass, scope, sinceIsoUtc, watermark, completion, cancellationToken))
            yield return obj;

        // Tombstone enumeration only on incremental runs — full runs treat absence
        // as "doesn't exist yet" and sinks reconcile separately.
        if (!isIncremental || string.IsNullOrWhiteSpace(sinceIsoUtc))
            yield break;

        await foreach (var tomb in EnumerateTombstonesAsync(objectClass, sinceIsoUtc!, cancellationToken))
            yield return tomb;
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateTombstonesAsync(
        string objectClass,
        string sinceIsoUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepo.GetByIdAsync(_tenantId);
        if (tenant is null) yield break;
        var creds = await ReadCredsAsync();
        if (creds is null) yield break;
        var (host, port) = ParseHostPort(tenant.Domain);

        LdapConnection connection;
        try { connection = CreateBoundConnection(host, port, creds); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD tombstone bind failed for tenant {TenantId}; skipping tombstone pass", _tenantId);
            yield break;
        }
        using var _conn = connection;

        // Resolve Deleted Objects container DN from RootDSE.
        string? deletedObjectsContainer;
        try
        {
            var rootReq = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                new[] { "deletedObjectsContainer", "defaultNamingContext" });
            var rootResp = (SearchResponse)connection.SendRequest(rootReq);
            if (rootResp.Entries.Count == 0) yield break;
            var entry = rootResp.Entries[0];
            deletedObjectsContainer = entry.Attributes.Contains("deletedObjectsContainer")
                ? entry.Attributes["deletedObjectsContainer"][0]?.ToString()
                : null;
            if (string.IsNullOrEmpty(deletedObjectsContainer) && entry.Attributes.Contains("defaultNamingContext"))
            {
                var dnc = entry.Attributes["defaultNamingContext"][0]?.ToString();
                if (!string.IsNullOrEmpty(dnc))
                    deletedObjectsContainer = $"CN=Deleted Objects,{dnc}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD RootDSE probe for deletedObjectsContainer failed; skipping tombstones");
            yield break;
        }
        if (string.IsNullOrEmpty(deletedObjectsContainer))
        {
            _logger.LogDebug("AD tenant {TenantId} did not advertise a deletedObjectsContainer — Recycle Bin likely disabled. Skipping tombstones.", _tenantId);
            yield break;
        }

        // Show-Deleted control (1.2.840.113556.1.4.417). Required to see isDeleted=TRUE objects.
        // Show-Deactivated-Link (1.2.840.113556.1.4.2065) is unrelated.
        var showDeletedControl = new DirectoryControl("1.2.840.113556.1.4.417", null, true, true);
        var generalized = ToGeneralizedTime(sinceIsoUtc);
        // Filter scoped to tombstones of the requested class, modified since cursor.
        var tombFilter = objectClass.ToLowerInvariant() switch
        {
            "user"     => $"(&(isDeleted=TRUE)(objectClass=user)(whenChanged>={generalized}))",
            "group"    => $"(&(isDeleted=TRUE)(objectClass=group)(whenChanged>={generalized}))",
            "computer" => $"(&(isDeleted=TRUE)(objectClass=computer)(whenChanged>={generalized}))",
            _          => $"(&(isDeleted=TRUE)(objectClass={AdQuery.Escape(objectClass)})(whenChanged>={generalized}))"
        };

        // Tombstones strip most attributes — only objectGUID, sAMAccountName, lastKnownParent etc remain.
        // Ask for the minimal viable set; rely on isDeleted + objectGUID to identify.
        var attrList = new[] { "objectGUID", "sAMAccountName", "isDeleted", "lastKnownParent", "whenChanged" };

        var pageControl = new PageResultRequestControl(500);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new SearchRequest(deletedObjectsContainer, tombFilter, SearchScope.Subtree, attrList);
            request.Controls.Add(pageControl);
            request.Controls.Add(showDeletedControl);

            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogWarning(ex, "AD tombstone search failed (filter {Filter}); Recycle Bin may not be enabled", tombFilter);
                yield break;
            }

            foreach (SearchResultEntry entry in response.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var obj = TombstoneToConnectorObject(entry, objectClass);
                if (obj is not null) yield return obj;
            }

            var responseControl = FindPageResponseControl(response.Controls);
            if (responseControl is null || responseControl.Cookie.Length == 0) yield break;
            pageControl.Cookie = responseControl.Cookie;
        }
    }

    private static ConnectorObject? TombstoneToConnectorObject(SearchResultEntry entry, string objectClass)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["distinguishedName"] = entry.DistinguishedName,
            ["_deleted"] = true,
        };
        foreach (string name in entry.Attributes.AttributeNames!)
        {
            var attr = entry.Attributes[name];
            if (attr.Count == 0) continue;
            attrs[name] = attr.Count == 1 ? attr[0] : (object)attr.GetValues(typeof(object));
        }

        string? sourceId = null;
        if (attrs.TryGetValue("objectGUID", out var g) && g is byte[] bytes && bytes.Length == 16)
            sourceId = new Guid(bytes).ToString();
        if (string.IsNullOrEmpty(sourceId))
            sourceId = entry.DistinguishedName;
        if (string.IsNullOrEmpty(sourceId)) return null;

        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = objectClass,
            Attributes = attrs
        };
    }

    private sealed class AdWatermark
    {
        public DateTime? Max { get; private set; }
        public void Observe(DateTime dt) { if (Max is null || dt > Max.Value) Max = dt; }
        public string IsoSafeOrNow() => (Max ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    /// <summary>
    /// Convert an ISO 8601 UTC timestamp string into AD generalizedTime format:
    /// YYYYMMDDHHMMSS.0Z (per RFC 4517 §3.3.13). Used to splice into LDAP filters
    /// for whenChanged comparisons.
    /// </summary>
    private static string ToGeneralizedTime(string isoUtc)
    {
        if (!DateTime.TryParse(isoUtc, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt)) dt = DateTime.UtcNow.AddDays(-30);
        return dt.ToString("yyyyMMddHHmmss") + ".0Z";
    }

    private async IAsyncEnumerable<ConnectorObject> EnumerateInternalAsync(
        string objectClass, SyncProjectScope scope, string? sinceIsoUtc, AdWatermark watermark,
        ReadCompletion? completion, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Conduit.SourceBrowsing.SourceBrowseScope.ValidateLists(scope.IncludedBaseDNs, scope.ExcludedBaseDNs);
        var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
            ?? throw new InvalidOperationException("The source connection is unavailable.");
        var creds = await ReadCredsAsync() ?? throw new InvalidOperationException("The selected source LDAP credential is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        var (host, port) = ParseHostPort(tenant.Domain);
        using var connection = await Task.Run(() => CreateBoundConnection(host, port, creds), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var filter = string.IsNullOrWhiteSpace(scope.LdapFilter) ? AdQuery.FilterForClass(objectClass) : scope.LdapFilter;
        if (!string.IsNullOrWhiteSpace(sinceIsoUtc)) filter = $"(&{filter}(whenChanged>={ToGeneralizedTime(sinceIsoUtc)}))";
        var query = new AdQuery
        {
            IncludedBases = scope.GetIncludedBaseList(), ExcludedBases = scope.GetExcludedBaseList(), Filter = filter,
            PageSize = Math.Clamp(scope.PageSize > 0 ? scope.PageSize : 1000, 1, 1000), MaximumRows = scope.MaxObjects,
            Attributes = scope.RequestedAttributes is { Count: > 0 }
                ? scope.RequestedAttributes.Where(a => !string.IsNullOrWhiteSpace(a)).Concat(StructuralAttributes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : []
        };
        var state = new AdReadState();
        await foreach (var record in AdReader.ReadAsync(new LdapSession(connection), query, state, cancellationToken))
        {
            if (record.Attributes.TryGetValue("whenChanged", out var changed) && changed is string raw && raw.Length >= 14 &&
                DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
                watermark.Observe(date);
            yield return EntryToConnectorObject(record, objectClass);
        }
        // Never set this on early consumer disposal, cancellation, failure, missing cookies or truncated ranges.
        if (completion != null) completion.IsComplete = state.IsComplete;
    }
    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId);
            if (tenant is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "Tenant not found." };

            var creds = await ReadCredsAsync();
            if (creds is null)
                return new ConnectorTestResult { IsSuccessful = false, Message = "No 'ldap' credential stored." };

            var (host, port) = ParseHostPort(tenant.Domain);
            using var connection = CreateBoundConnection(host, port, creds);

            // RootDSE probe.
            var req = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                new[] { "defaultNamingContext", "currentTime" });
            var resp = (SearchResponse)connection.SendRequest(req);
            var dnc = resp.Entries.Count > 0 && resp.Entries[0].Attributes.Contains("defaultNamingContext")
                ? resp.Entries[0].Attributes["defaultNamingContext"][0]?.ToString()
                : null;

            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Bound to {host}:{port}. defaultNamingContext={dnc ?? "(unknown)"}."
            };
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private sealed record AdCredentials(string Username, string Password);

    /// <summary>
    /// Build a bound LdapConnection using Negotiate (GSS-SPNEGO → Kerberos/NTLM).
    /// Domain controllers that enforce LDAP signing/sealing REJECT a simple bind
    /// (AuthType.Basic) over plain :389 — IC's DirectoryQueryService uses Negotiate
    /// for exactly this reason. We mirror it here so Conduit binds to a hardened DC
    /// standalone. DOMAIN\user is split into NetworkCredential(user){Domain} the way
    /// SSPI expects; UPN/bare names pass through unchanged.
    /// </summary>
    private static LdapConnection CreateBoundConnection(string host, int port, AdCredentials creds)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Negotiate,
            Timeout = TimeSpan.FromSeconds(30)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        // Single-domain read: never chase referrals. ReferralChasingOptions.All
        // makes the DC hand back continuation referrals (subordinate/external
        // naming contexts), and S.DS.P then tries to bind+search each one — DNS
        // resolution, fresh binds, and timeouts that add seconds-to-minutes of
        // dead latency per page on a Subtree search. We read exactly the included
        // bases we were given, so suppress chasing entirely.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        NetworkCredential netCred;
        if (creds.Username.Contains('\\'))
        {
            var parts = creds.Username.Split('\\', 2);
            netCred = new NetworkCredential(parts[1], creds.Password) { Domain = parts[0] };
        }
        else
        {
            netCred = new NetworkCredential(creds.Username, creds.Password);
        }
        connection.Credential = netCred;
        try { connection.Bind(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private async Task<AdCredentials?> ReadCredsAsync()
    {
        var name = Conduit.Sync.Security.CredentialNameContext.Resolve("ldap", Conduit.Sync.Security.CredentialSide.Source);
        var raw = await _protector.RetrieveAsync(_tenantId, name);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var doc = JsonDocument.Parse(raw);
            var u = doc.RootElement.TryGetProperty("Username", out var uEl) ? uEl.GetString() : null;
            var p = doc.RootElement.TryGetProperty("Password", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(u) || p is null) return null;
            return new AdCredentials(u, p);
        }
        catch
        {
            return null;
        }
    }

    private static (string Host, int Port) ParseHostPort(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Tenant.Domain is required for AD connections (host or host:port).");
        var parts = domain.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }

    private static ConnectorObject EntryToConnectorObject(AdRecord entry, string objectClass)
    {
        // Preserve Conduit's value shape and derived fields after the common raw LDAP read.
        var attrs = entry.Attributes.ToDictionary(p => p.Key, p => p.Value is object?[] values ? (object?)values.ToList() : p.Value, StringComparer.OrdinalIgnoreCase);
        // Source ID priority: objectGUID > distinguishedName.
        var sourceId = attrs.TryGetValue("objectGUID", out var g) && g is byte[] bytes && bytes.Length == 16
            ? new Guid(bytes).ToString()
            : entry.DistinguishedName;

        // Derived enabled-ness: AD stores it as bit 0x2 (ACCOUNTDISABLE) of the
        // userAccountControl bitmask, which no sink understands natively. Surface it as
        // the standard "isActive" attribute (same name the cloud sources emit) so a
        // mapped isActive -> IsActive flows the real enabled state to the sink. Without
        // this every AD-synced account's enabled-ness was whatever the sink defaulted.
        if (attrs.TryGetValue("userAccountControl", out var uacRaw)
            && uacRaw is string uacStr && int.TryParse(uacStr, out var uac))
        {
            attrs["isActive"] = (uac & 0x2) == 0 ? "true" : "false";
        }

        // Last-logon normalization: AD stores lastLogonTimestamp / lastLogon as a
        // Windows FILETIME (100ns ticks since 1601-01-01, an 18-digit integer) that no
        // sink or SQL date function understands. Convert to an ISO-8601 UTC string here
        // so IC lands a real datetime, not junk that TRY_CONVERT silently drops. Prefer
        // lastLogonTimestamp (replicated, ~9-14 day lazy accuracy); lastLogon is per-DC
        // and non-replicated, so it's only a fallback when the replicated value is absent.
        var logonIso = ConvertAdFileTime(attrs.TryGetValue("lastLogonTimestamp", out var llt) ? llt : null)
                    ?? ConvertAdFileTime(attrs.TryGetValue("lastLogon", out var ll) ? ll : null);
        if (logonIso != null)
            attrs["lastLogonTimestamp"] = logonIso;

        // pwdLastSet is the same FILETIME shape and needs the same treatment — a
        // password-age rule reading an 18-digit integer as a date silently evaluates
        // nothing. ConvertAdFileTime returns null for the 0 sentinel ("must change at
        // next logon"), so that case lands as no value rather than a bogus 1601 date.
        if (attrs.TryGetValue("pwdLastSet", out var pls))
        {
            var pwdIso = ConvertAdFileTime(pls);
            if (pwdIso != null)
                attrs["pwdLastSet"] = pwdIso;
        }

        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = objectClass,
            Attributes = attrs
        };
    }

    /// <summary>
    /// Convert an AD FILETIME attribute value (100ns ticks since 1601, as an 18-digit
    /// string) to an ISO-8601 UTC string ("o"). Returns null for absent / unparseable /
    /// the sentinel "never" values (0 and 0x7FFFFFFFFFFFFFFF), and passes an already-ISO
    /// datetime through unchanged so this is safe to call on either shape.
    /// </summary>
    private static string? ConvertAdFileTime(object? raw)
    {
        if (raw is not string s || string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, out var ticks))
        {
            // 0 = never logged on; 0x7FFFFFFFFFFFFFFF = the AD "never expires" sentinel.
            if (ticks <= 0 || ticks == long.MaxValue) return null;
            try { return DateTime.FromFileTimeUtc(ticks).ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        // Already a datetime string (e.g. re-processed value) -> normalize to UTC ISO.
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
            return dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private static object? ConvertAttributeValue(object? raw)
    {
        // S.DS.P returns strings or byte[]. Strings pass through; binary attributes
        // (objectGUID, objectSid, ntSecurityDescriptor, etc.) stay as byte[] for the
        // mapper or sink to handle.
        return raw;
    }

    private static PageResultResponseControl? FindPageResponseControl(DirectoryControl[] controls)
    {
        foreach (var c in controls)
            if (c is PageResultResponseControl p) return p;
        return null;
    }
}
