namespace Conduit.Shared.SyncUI;

/// <summary>
/// How the shared <c>ScopeTree</c> behaves.
///
/// <see cref="Multi"/> (DEFAULT) is the IC-parity 4-state model: Included / Blocked /
/// Inherited / NotSelected, two-way bound on the Included + Blocked DN sets. This is
/// what IdentityCenter and Conduit's "Select Scope Locations" modal use — the default
/// MUST stay Multi so neither host regresses.
///
/// <see cref="Single"/> is the Base DN picker: exactly ONE container is selectable
/// (radio-style — picking another clears the prior). There is no Blocked state. The
/// chosen DN is surfaced via <c>SelectedDistinguishedName</c> + <c>OnSingleSelected</c>.
/// Same tree rendering, icons, lazy drill-in, and data source as Multi.
/// </summary>
public enum ScopeSelectionMode
{
    /// <summary>IC-parity 4-state multi-select. The default.</summary>
    Multi = 0,

    /// <summary>Single-container radio-style select for a Base DN. No Blocked state.</summary>
    Single = 1,
}

/// <summary>
/// The four scope states of a container in the sync scope tree — faithful to
/// IdentityCenter's MultiScopeBrowser model.
///
/// EXPLICIT states (persisted): <see cref="Included"/>, <see cref="Blocked"/>.
/// COMPUTED state (never persisted): <see cref="Inherited"/> — a node is Inherited
/// when an ANCESTOR DN is explicitly Included and the node itself is neither
/// Included nor Blocked. The component derives this from the Included set at render
/// time, exactly like IC; it is never stored as its own entry.
///
/// The click cycle on the explicit state is:
///   NotSelected → Included → Blocked → NotSelected
/// Inherited is not part of the click cycle — it falls out of an Included ancestor.
/// </summary>
public enum ScopeState
{
    /// <summary>Neither included nor blocked, and no Included ancestor. Not in scope.</summary>
    NotSelected = 0,

    /// <summary>Explicitly included. Read as a subtree. (Persisted.)</summary>
    Included = 1,

    /// <summary>
    /// Computed: an ancestor DN is explicitly Included, so this node is in scope by
    /// inheritance. Never persisted — re-derived at read time.
    /// </summary>
    Inherited = 2,

    /// <summary>Explicitly blocked. This node's subtree is pruned even under an Included ancestor. (Persisted.)</summary>
    Blocked = 3,
}
