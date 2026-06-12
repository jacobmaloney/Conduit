using System;
using System.Collections;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Active Roles source via the AR ADSI provider (EDMS:// + DirectorySearcher).
///
/// Reading through EDMS:// routes the search through the AR Administration
/// Service, so the objects come back with their Active Roles VIRTUAL ATTRIBUTES
/// resolved (e.g. the boolean role VAs) — which a raw-AD read would never see.
/// That is the Phase-1 source proof: read a user and observe a virtual-attribute
/// value alongside the real ones.
///
/// Phase 1 is a single, non-incremental subtree read. Phase 2 adds the fast
/// direct path (raw LDAP + ARS SQL CVSAValues) and an incremental cursor.
/// </summary>
public sealed class ActiveRolesSource : IConnectorSource
{
    private readonly IArsConnectionResolver _resolver;
    private readonly ILogger<ActiveRolesSource> _logger;

    public ActiveRolesSource(IArsConnectionResolver resolver, ILogger<ActiveRolesSource> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public async IAsyncEnumerable<ConnectorObject> ReadAsync(
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = await _resolver.ResolveAsync(CredentialSide.Source, cancellationToken);
        if (settings is null)
            throw new InvalidOperationException(
                "No 'ars' credential resolved. Save Active Roles bind credentials before running.");

        // ─── Phase 2 FAST PATH: raw AD LDAP + CVSAValues SQL join ──────────────
        // Default read mode is "fast". It needs both a DC (adHost / arsServiceHost)
        // and the ARS config-DB connection string. When requested but not fully
        // configured, fall back to the policy (EDMS://) read with a logged warning.
        if (!settings.IsPolicyRead)
        {
            if (settings.CanFastRead)
            {
                await foreach (var obj in ReadFastAsync(settings, objectClass, scope, cancellationToken))
                    yield return obj;
                yield break;
            }

            _logger.LogWarning(
                "ARS source: readMode is '{Mode}' (fast preferred) but the fast path is not fully configured " +
                "(adHost='{AdHost}', arsSqlConnString {SqlState}). Falling back to the policy (EDMS://) read.",
                settings.ReadMode ?? "fast",
                settings.AdHost ?? settings.ArsServiceHost ?? "<none>",
                string.IsNullOrWhiteSpace(settings.ArsSqlConnString) ? "missing" : "present");
        }

        // ─── POLICY PATH (legacy, Phase 1): EDMS:// DirectorySearcher + per-object VA resolve ───
        var baseDn = scope.GetIncludedBaseList() is { Count: > 0 } bases ? bases[0] : scope.BaseDN;
        if (string.IsNullOrWhiteSpace(baseDn))
            throw new InvalidOperationException(
                "Active Roles source (policy read) requires an explicit Base DN on the project scope.");

        var filter = BuildFilter(objectClass, scope.LdapFilter);
        var max = scope.MaxObjects ?? int.MaxValue;

        // DirectorySearcher enumeration is synchronous COM; we materialize one
        // page-bounded result set and yield from it.
        foreach (var obj in Enumerate(settings, baseDn!, filter, objectClass, scope, max, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return obj;
        }
    }

    /// <summary>
    /// Phase 2 fast read. Streams real attributes from a DC via
    /// <see cref="FastAdReader"/> (paged S.DS.Protocols, ms latency) and merges
    /// Active Roles virtual attributes read in bulk from the ARS config DB's
    /// <c>CVSAValues</c> table (<see cref="CvsaValueReader"/>) — bypassing the AR
    /// service entirely. Objects are buffered in windows so VAs for a whole window
    /// resolve in ONE SQL round-trip, then emitted with VAs merged in.
    ///
    /// VA values are typed to MATCH the policy (EDMS://) path — Boolean VAs come
    /// back as a CLR <see cref="bool"/> — so the downstream attribute mapping is
    /// identical regardless of read mode.
    /// </summary>
    private async IAsyncEnumerable<ConnectorObject> ReadFastAsync(
        ArsConnectionSettings settings,
        string objectClass,
        SyncProjectScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostPort(settings.AdHost is { Length: > 0 } ? settings.AdHost : settings.ArsServiceHost);
        var adReader = new FastAdReader(host, port, settings.BindUser, settings.BindPassword, _logger);
        var cvsa = new CvsaValueReader(settings.ArsSqlConnString!, _logger);

        // Pre-load the VirtualSchema map once so the per-window SQL is just the value query.
        await cvsa.EnsureSchemaMapAsync(cancellationToken);

        const int windowSize = 1000;
        var window = new List<ConnectorObject>(windowSize);

        _logger.LogInformation(
            "ARS fast read: enumerating class '{Class}' from DC {Host}:{Port}, joining virtual attributes from CVSAValues.",
            objectClass, host, port);

        await foreach (var obj in adReader.ReadAsync(objectClass, scope, cancellationToken))
        {
            window.Add(obj);
            if (window.Count >= windowSize)
            {
                foreach (var merged in await MergeWindowAsync(cvsa, window, cancellationToken))
                    yield return merged;
                window.Clear();
            }
        }
        if (window.Count > 0)
        {
            foreach (var merged in await MergeWindowAsync(cvsa, window, cancellationToken))
                yield return merged;
        }
    }

    /// <summary>
    /// Resolve the virtual attributes for a window of objects in one SQL query and
    /// merge them onto each object by objectGUID. Objects whose SourceId is a GUID
    /// (the AD source's normal case) participate in the join; a non-GUID SourceId
    /// (e.g. a DN fallback) simply gets no VAs.
    /// </summary>
    private static async Task<IReadOnlyList<ConnectorObject>> MergeWindowAsync(
        CvsaValueReader cvsa, List<ConnectorObject> window, CancellationToken cancellationToken)
    {
        var guids = new List<Guid>(window.Count);
        var byGuid = new Dictionary<Guid, ConnectorObject>();
        foreach (var obj in window)
        {
            if (Guid.TryParse(obj.SourceId, out var g))
            {
                guids.Add(g);
                byGuid[g] = obj;
            }
        }

        if (guids.Count > 0)
        {
            var vaMap = await cvsa.ReadVirtualAttributesAsync(guids, cancellationToken);
            foreach (var kvp in vaMap)
            {
                if (!byGuid.TryGetValue(kvp.Key, out var obj)) continue;
                foreach (var va in kvp.Value)
                    obj.Attributes[va.Key] = va.Value;
            }
        }

        return window;
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await _resolver.ResolveAsync(CredentialSide.Source, cancellationToken);
        if (settings is null)
            return new ConnectorTestResult { IsSuccessful = false, Message = "No 'ars' credential resolved." };
        return ArsProbe.Test(settings, _logger);
    }

    private IEnumerable<ConnectorObject> Enumerate(
        ArsConnectionSettings settings,
        string baseDn,
        string filter,
        string objectClass,
        SyncProjectScope scope,
        int max,
        CancellationToken cancellationToken)
    {
        DirectoryEntry root;
        DirectorySearcher searcher;
        SearchResultCollection results;
        try
        {
            root = ArsBind.Bind(settings, baseDn);
            searcher = new DirectorySearcher(root)
            {
                Filter = filter,
                SearchScope = SearchScope.Subtree,
                PageSize = scope.PageSize > 0 ? scope.PageSize : 1000
            };

            // Load the requested attributes if the orchestrator hinted them; ALWAYS
            // union the structural floor. When no hint is present we leave
            // PropertiesToLoad empty so the provider returns its default attribute
            // set (which already includes resolved virtual attributes).
            if (scope.RequestedAttributes is { Count: > 0 })
            {
                foreach (var a in StructuralAttributes) searcher.PropertiesToLoad.Add(a);
                foreach (var a in scope.RequestedAttributes) searcher.PropertiesToLoad.Add(a);
            }

            results = searcher.FindAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARS source enumeration failed under {BaseDn}", baseDn);
            throw;
        }

        using (root)
        using (searcher)
        using (results)
        {
            // Attributes the orchestrator/CLI explicitly asked for that the
            // DirectorySearcher projection did NOT return are likely Active Roles
            // virtual attributes — the searcher does not surface them. Resolve those
            // per-object by binding EDMS://<dn> and reading Properties (the proven
            // through-ARS read path). NULL hint = take whatever the searcher returned.
            var wanted = scope.RequestedAttributes;

            var emitted = 0;
            foreach (SearchResult result in results)
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                if (emitted >= max) yield break;

                var obj = MapResult(result, objectClass);
                if (obj is null) continue;

                if (wanted is { Count: > 0 })
                {
                    var missing = new List<string>();
                    foreach (var a in wanted)
                        if (!obj.Attributes.ContainsKey(a)) missing.Add(a);

                    if (missing.Count > 0)
                    {
                        var resolved = ArsProbe.ResolveAttributes(settings, obj.SourceId, missing, _logger);
                        foreach (var kvp in resolved) obj.Attributes[kvp.Key] = kvp.Value;
                    }
                }

                emitted++;
                yield return obj;
            }
        }
    }

    private static ConnectorObject? MapResult(SearchResult result, string objectClass)
    {
        var attrs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (string propName in result.Properties.PropertyNames)
        {
            var values = result.Properties[propName];
            if (values.Count == 0) continue;
            if (values.Count == 1)
            {
                attrs[propName] = values[0];
            }
            else
            {
                var list = new List<object?>(values.Count);
                foreach (var v in values) list.Add(v);
                attrs[propName] = list;
            }
        }

        var sourceId = FirstString(result, "distinguishedName");
        if (string.IsNullOrEmpty(sourceId))
        {
            // Strip the EDMS:// moniker off the path to recover the DN.
            var path = result.Path;
            var idx = path.IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = path.Substring(idx + 2);
                var slash = rest.IndexOf('/');
                sourceId = slash >= 0 ? rest.Substring(slash + 1) : rest;
            }
        }
        if (string.IsNullOrEmpty(sourceId)) return null;

        if (!attrs.ContainsKey("distinguishedName")) attrs["distinguishedName"] = sourceId;

        return new ConnectorObject
        {
            SourceId = sourceId!,
            ObjectClass = string.IsNullOrWhiteSpace(objectClass) ? "user" : objectClass,
            Attributes = attrs
        };
    }

    private static string? FirstString(SearchResult result, string prop)
    {
        if (!result.Properties.Contains(prop)) return null;
        var values = result.Properties[prop];
        return values.Count > 0 ? values[0]?.ToString() : null;
    }

    /// <summary>
    /// Compose the LDAP filter. When the project supplies one it is AND-ed with the
    /// object-class gate so a scope filter never silently widens the class.
    /// </summary>
    private static string BuildFilter(string objectClass, string? scopeFilter)
    {
        var cls = string.IsNullOrWhiteSpace(objectClass) ? "user" : objectClass.Trim();
        var classGate = $"(objectClass={cls})";
        if (string.IsNullOrWhiteSpace(scopeFilter)) return classGate;
        var sf = scopeFilter.Trim();
        if (!sf.StartsWith('(')) sf = $"({sf})";
        return $"(&{classGate}{sf})";
    }

    private static readonly string[] StructuralAttributes =
    {
        "distinguishedName",
        "objectGUID",
        "objectClass",
        "sAMAccountName",
        "userPrincipalName",
    };

    /// <summary>
    /// Split a "host" or "host:port" hint into (host, port). Defaults to LDAP 389
    /// (the fast read targets the raw DC, NOT the AR service port). Used by the
    /// fast path to bind the DC.
    /// </summary>
    private static (string Host, int Port) ParseHostPort(string? hostHint)
    {
        if (string.IsNullOrWhiteSpace(hostHint))
            throw new InvalidOperationException(
                "Active Roles fast read requires a DC host (adHost, or arsServiceHost as a fallback).");
        var parts = hostHint.Split(':');
        if (parts.Length == 1) return (parts[0], 389);
        if (int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (parts[0], 389);
    }
}
