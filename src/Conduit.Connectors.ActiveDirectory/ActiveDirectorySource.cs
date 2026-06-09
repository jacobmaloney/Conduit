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
/// Phase 1A keeps it deliberately small: simple bind, paged search, one
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
            _          => $"(&(isDeleted=TRUE)(whenChanged>={generalized}))"
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
        string objectClass,
        SyncProjectScope scope,
        string? sinceIsoUtc,
        AdWatermark watermark,
        ReadCompletion? completion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepo.GetByIdAsync(_tenantId)
            ?? throw new InvalidOperationException($"Tenant {_tenantId} not found.");

        var (host, port) = ParseHostPort(tenant.Domain);
        var baseFilter = !string.IsNullOrWhiteSpace(scope.LdapFilter)
            ? scope.LdapFilter!
            : DefaultFilterForClass(objectClass);
        // Phase 2: splice whenChanged>=cursor onto the base filter when incremental.
        var ldapFilter = !string.IsNullOrWhiteSpace(sinceIsoUtc)
            ? $"(&{baseFilter}(whenChanged>={ToGeneralizedTime(sinceIsoUtc!)}))"
            : baseFilter;

        // IC-parity multi-select scope. GetIncludedBaseList() returns the explicit
        // Included DN set (or the legacy single BaseDN, or empty). Each Included DN
        // is read as a Subtree. Excluded DNs prune any entry at/under them — exactly
        // IC's SearchBases / ExcludedSearchBases model.
        var includedBases = OptimizeBases(scope.GetIncludedBaseList());
        var excludedBases = scope.GetExcludedBaseList();

        var creds = await ReadCredsAsync()
            ?? throw new InvalidOperationException(
                $"No 'ldap' credential stored for tenant {_tenantId}. Save credentials before running.");

        using var connection = CreateBoundConnection(host, port, creds);

        // IC-parity empty-scope fallback. IC's DirectoryQueryService treats a blank
        // SearchBase as "sync the whole domain" by resolving the directory's
        // defaultNamingContext from RootDSE at run time (DirectoryQueryService
        // .GetDefaultNamingContextAnonymous). Conduit auto-generated projects (and
        // any manual project where the operator picked no container) arrive with an
        // empty included set, so mirror IC here rather than hard-failing: bind, read
        // RootDSE.defaultNamingContext, and read the whole domain root as a Subtree.
        // Only when the directory advertises no naming context do we throw.
        if (includedBases.Count == 0)
        {
            var rootDn = ResolveDefaultNamingContext(connection);
            if (string.IsNullOrWhiteSpace(rootDn))
                throw new InvalidOperationException(
                    "AD source has no included Base DN and the directory did not advertise a defaultNamingContext on RootDSE. Pick an Included container in the scope tree.");
            _logger.LogInformation(
                "AD source for tenant {TenantId} had an empty scope; defaulting to the domain root {RootDn} (RootDSE defaultNamingContext), matching IC's whole-domain default.",
                _tenantId, rootDn);
            includedBases = new List<string> { rootDn! };
        }

        // RFC 2696 paged results. PageSize bounded by scope; default 1000.
        var pageSize = scope.PageSize > 0 ? scope.PageSize : 1000;
        var emitted = 0;

        // Attribute projection. When the orchestrator stamped the step's mapped
        // SOURCE attributes onto scope.RequestedAttributes, request ONLY those plus
        // the structural floor — NOT all attributes. Pulling every attribute (the
        // old attributeList:null) costs ~4x on the wire and forces the DC to
        // materialize expensive constructed/operational attributes per entry, and
        // makes EntryToConnectorObject iterate a far larger attribute bag. A null
        // hint preserves the old read-all behavior for any caller that doesn't set
        // it. The structural set is ALWAYS unioned in so trimming can't break
        // SourceId / cursor / class gating.
        string[]? attributeList = null;
        if (scope.RequestedAttributes is { Count: > 0 })
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in scope.RequestedAttributes)
                if (!string.IsNullOrWhiteSpace(a)) set.Add(a.Trim());
            foreach (var s in StructuralAttributes) set.Add(s);
            attributeList = set.ToArray();
            _logger.LogInformation(
                "AD source for tenant {TenantId} requesting a projected {Count}-attribute set (mapped + structural) for class {Class} instead of all attributes.",
                _tenantId, attributeList.Length, objectClass);
        }
        else
        {
            _logger.LogInformation(
                "AD source for tenant {TenantId} has no attribute projection hint; reading ALL attributes for class {Class} (slower).",
                _tenantId, objectClass);
        }

        // Walk each Included base in turn. The complete-read sentinel may ONLY be
        // set after the FINAL base drains to its natural LDAP terminus, so a partial
        // drain of any earlier base never green-lights tombstones.
        for (int baseIdx = 0; baseIdx < includedBases.Count; baseIdx++)
        {
            var searchBase = includedBases[baseIdx];
            var isLastBase = baseIdx == includedBases.Count - 1;
            var pageControl = new PageResultRequestControl(pageSize);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new SearchRequest(
                    searchBase,
                    ldapFilter,
                    SearchScope.Subtree,
                    attributeList);   // null = all attributes; else mapped + structural projection
                request.Controls.Add(pageControl);

                SearchResponse response;
                try
                {
                    response = (SearchResponse)connection.SendRequest(request);
                }
                catch (DirectoryOperationException ex)
                {
                    _logger.LogError(ex, "LDAP search failed for tenant {TenantId} (base {Base}, filter {Filter})",
                        _tenantId, searchBase, ldapFilter);
                    throw;
                }

                foreach (SearchResultEntry entry in response.Entries)
                {
                    if (scope.MaxObjects.HasValue && emitted >= scope.MaxObjects.Value)
                        // Deliberate early exit on the MaxObjects cap. This is a TRUNCATED
                        // read, not a complete drain — leave completion.IsComplete FALSE so
                        // the orchestrator does NOT tombstone against a partial population.
                        yield break;

                    // Blocked-subtree prune: drop any entry whose DN is at/under an
                    // Excluded base, even though it sits under an Included base. This
                    // is what makes "Explicitly Blocked" functional, not cosmetic.
                    if (IsInExcludedScope(entry.DistinguishedName, excludedBases))
                        continue;

                    // Phase 2 cursor: track max whenChanged seen.
                    if (entry.Attributes.Contains("whenChanged"))
                    {
                        var raw = entry.Attributes["whenChanged"][0]?.ToString();
                        if (!string.IsNullOrEmpty(raw))
                        {
                            // AD generalizedTime: yyyyMMddHHmmss.0Z
                            if (raw.Length >= 14 && DateTime.TryParseExact(raw.Substring(0, 14),
                                "yyyyMMddHHmmss", null,
                                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                out var dt))
                            {
                                watermark.Observe(dt);
                            }
                        }
                    }

                    var obj = EntryToConnectorObject(entry, objectClass);
                    emitted++;
                    yield return obj;
                }

                // Advance the cookie. Empty cookie = last page of THIS base.
                var responseControl = FindPageResponseControl(response.Controls);
                if (responseControl is null || responseControl.Cookie.Length == 0)
                {
                    // This base drained naturally. Only the LAST base's natural
                    // terminus proves the WHOLE read is complete — that is the ONLY
                    // site that may set the sentinel. (A thrown DirectoryOperationException
                    // rethrows before reaching here; a MaxObjects truncation yield-breaks
                    // before reaching here; a cancellation throws OperationCanceledException.)
                    if (isLastBase && completion is not null) completion.IsComplete = true;
                    break; // move to the next Included base (or finish)
                }
                pageControl.Cookie = responseControl.Cookie;
            }
        }
    }

    /// <summary>
    /// IC-parity redundant-child removal. When an Included base is already covered
    /// by a shorter (ancestor) Included base, drop it so its subtree isn't read
    /// twice. Mirrors IC's DirectoryQueryService optimization.
    /// </summary>
    private static List<string> OptimizeBases(List<string> bases)
    {
        var optimized = new List<string>();
        foreach (var b in bases.Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select(s => s.Trim())
                                .OrderBy(s => s.Length))
        {
            bool redundant = optimized.Any(parent =>
                b.EndsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                b.Length > parent.Length);
            if (!redundant)
                optimized.Add(b);
        }
        return optimized;
    }

    /// <summary>
    /// IC-parity whole-domain default. When a scope carries no included Base DN,
    /// resolve the directory's domain root by reading RootDSE.defaultNamingContext
    /// (with rootDomainNamingContext as the fallback, exactly like IC's
    /// DirectoryQueryService.GetDefaultNamingContextAnonymous). Returns null when
    /// neither attribute is advertised so the caller can fail loud rather than
    /// silently enumerate nothing. The connection is already bound.
    /// </summary>
    private string? ResolveDefaultNamingContext(LdapConnection connection)
    {
        try
        {
            var rootReq = new SearchRequest("", "(objectClass=*)", SearchScope.Base,
                new[] { "defaultNamingContext", "rootDomainNamingContext" });
            var rootResp = (SearchResponse)connection.SendRequest(rootReq);
            if (rootResp.Entries.Count == 0) return null;
            var attrs = rootResp.Entries[0].Attributes;
            if (attrs.Contains("defaultNamingContext"))
            {
                var v = attrs["defaultNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            if (attrs.Contains("rootDomainNamingContext"))
            {
                var v = attrs["rootDomainNamingContext"][0]?.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AD RootDSE defaultNamingContext probe failed for tenant {TenantId}", _tenantId);
            return null;
        }
    }

    /// <summary>
    /// IC-parity blocked-subtree test: true when <paramref name="dn"/> is at or
    /// under any Excluded base (case-insensitive DN-suffix match). Mirrors IC's
    /// DirectorySchemaService.IsInExcludedScope.
    /// </summary>
    private static bool IsInExcludedScope(string? dn, List<string> excludedBases)
    {
        if (string.IsNullOrEmpty(dn) || excludedBases.Count == 0)
            return false;
        foreach (var excluded in excludedBases)
        {
            if (!string.IsNullOrWhiteSpace(excluded) &&
                dn.EndsWith(excluded.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
            AuthType = AuthType.Negotiate
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
        connection.Bind();
        return connection;
    }

    private async Task<AdCredentials?> ReadCredsAsync()
    {
        var name = Conduit.Sync.Security.CredentialNameContext.Resolve("ldap", Conduit.Sync.Security.CredentialSide.Source);
        var raw = await _protector.RetrieveAsync(_tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = Conduit.Sync.Security.CredentialNameContext.Resolve("ldap", Conduit.Sync.Security.CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await _protector.RetrieveAsync(_tenantId, sinkName);
        }
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

    private static string DefaultFilterForClass(string objectClass) => objectClass.ToLowerInvariant() switch
    {
        "user"     => "(&(objectClass=user)(objectCategory=person))",
        "group"    => "(objectCategory=group)",
        "computer" => "(objectCategory=computer)",
        "contact"  => "(objectClass=contact)",
        _          => "(objectClass=*)"
    };

    private static ConnectorObject EntryToConnectorObject(SearchResultEntry entry, string objectClass)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["distinguishedName"] = entry.DistinguishedName
        };

        foreach (string name in entry.Attributes.AttributeNames!)
        {
            var attr = entry.Attributes[name];
            if (attr.Count == 0) { attrs[name] = null; continue; }
            if (attr.Count == 1)
            {
                attrs[name] = ConvertAttributeValue(attr[0]);
            }
            else
            {
                var list = new List<object?>(attr.Count);
                for (int i = 0; i < attr.Count; i++) list.Add(ConvertAttributeValue(attr[i]));
                attrs[name] = list;
            }
        }

        // Source ID priority: objectGUID > distinguishedName.
        var sourceId = attrs.TryGetValue("objectGUID", out var g) && g is byte[] bytes && bytes.Length == 16
            ? new Guid(bytes).ToString()
            : entry.DistinguishedName;

        return new ConnectorObject
        {
            SourceId = sourceId,
            ObjectClass = objectClass,
            Attributes = attrs
        };
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
