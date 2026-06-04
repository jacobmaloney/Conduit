namespace Conduit.Shared.SyncUI;

/// <summary>
/// The single seam the shared scope component depends on. Each host app supplies
/// a thin adapter:
///   • IdentityCenter → over DirectorySchemaService.GetOrganizationalUnitsAsync
///   • Conduit        → over the connector's IConnectorContainerBrowser (live bind)
///
/// The component knows nothing about connections, tenants, credentials, or LDAP —
/// it asks this seam for one level of children at a time and renders the 4-state
/// scope tree. This is what makes IC and Conduit run the SAME component code.
/// </summary>
public interface IScopeDataSource
{
    /// <summary>
    /// Load the immediate child containers under <paramref name="parentDn"/>.
    /// Pass null/empty to load the ROOT level (the adapter resolves the directory's
    /// default naming context / domain root).
    ///
    /// Implementations should NEVER throw for an expected directory error — return
    /// a <see cref="ScopeLoadResult"/> with <see cref="ScopeLoadResult.ErrorMessage"/>
    /// set so the component renders a clean inline message instead of a bare tree.
    /// (A thrown exception is also caught by the component as a last resort.)
    /// </summary>
    Task<ScopeLoadResult> LoadChildrenAsync(string? parentDn, CancellationToken cancellationToken);
}

/// <summary>
/// Envelope returned by <see cref="IScopeDataSource.LoadChildrenAsync"/>. Carries
/// the child nodes, the resolved root DN (useful when the caller passed null and
/// the directory substituted its defaultNamingContext), and an optional error
/// string so load failures surface cleanly rather than being swallowed.
/// </summary>
public sealed class ScopeLoadResult
{
    public IReadOnlyList<ScopeNode> Nodes { get; init; } = Array.Empty<ScopeNode>();

    /// <summary>
    /// The DN actually enumerated. Set on a ROOT load so the component can build
    /// the synthetic domain-root node and default the initial Included selection
    /// to it (matching IC's "default to domain root when nothing selected").
    /// </summary>
    public string? ResolvedRootDn { get; init; }

    /// <summary>Optional friendly label for the root node (e.g. the domain name). Falls back to a DN-derived label.</summary>
    public string? RootDisplayName { get; init; }

    /// <summary>Non-null when the load failed; the component shows this instead of nodes.</summary>
    public string? ErrorMessage { get; init; }

    public static ScopeLoadResult Ok(IReadOnlyList<ScopeNode> nodes, string? resolvedRootDn = null, string? rootDisplayName = null) =>
        new() { Nodes = nodes, ResolvedRootDn = resolvedRootDn, RootDisplayName = rootDisplayName };

    public static ScopeLoadResult Fail(string message) =>
        new() { ErrorMessage = message };
}
