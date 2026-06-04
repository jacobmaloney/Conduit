namespace Conduit.Shared.SyncUI;

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
