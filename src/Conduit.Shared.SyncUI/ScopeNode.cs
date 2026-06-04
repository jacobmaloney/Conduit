namespace Conduit.Shared.SyncUI;

/// <summary>
/// One container node in the scope tree. Neutral — carries only what the shared
/// component needs to render and to identify a container by DN. Each host app
/// maps its own directory-node type onto this in its <see cref="IScopeDataSource"/>
/// adapter (IC's OrganizationalUnitInfo, Conduit's DirectoryContainerNode).
///
/// Children are loaded lazily on expand via <see cref="IScopeDataSource"/>; the
/// component owns the loaded/expanded UI state, so this type stays a plain DTO.
/// </summary>
public sealed class ScopeNode
{
    /// <summary>Short display name (the ou/cn/name attribute).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full distinguished name. This is the scope key — what gets persisted into
    /// the Included / Blocked sets and used for ancestor (inheritance) matching.
    /// </summary>
    public string Dn { get; set; } = string.Empty;

    /// <summary>Optional description, shown muted beside the name.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True if this node may contain further child containers. Advisory — the
    /// component shows a chevron and resolves children only on first expand, so an
    /// always-true value that reveals an empty level on drill-in is acceptable
    /// (matches IC).
    /// </summary>
    public bool HasChildren { get; set; } = true;

    /// <summary>
    /// Node kind for the type icon: "domain", "ou", or "cn". Defaults to "ou".
    /// The synthetic root node the component builds is "domain".
    /// </summary>
    public string NodeType { get; set; } = "ou";
}
