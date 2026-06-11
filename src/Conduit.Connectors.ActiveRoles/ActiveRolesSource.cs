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

        var baseDn = scope.GetIncludedBaseList() is { Count: > 0 } bases ? bases[0] : scope.BaseDN;
        if (string.IsNullOrWhiteSpace(baseDn))
            throw new InvalidOperationException(
                "Active Roles source (Phase 1) requires an explicit Base DN on the project scope.");

        var filter = BuildFilter(objectClass, scope.LdapFilter);
        var max = scope.MaxObjects ?? int.MaxValue;

        // DirectorySearcher enumeration is synchronous COM; we materialize one
        // page-bounded result set and yield from it. Phase 2 will stream pages.
        foreach (var obj in Enumerate(settings, baseDn!, filter, objectClass, scope, max, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return obj;
        }
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
}
