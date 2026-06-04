using Conduit.Shared.SyncUI;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;

namespace Conduit.Web.Shared.Design;

/// <summary>
/// Conduit's adapter from the shared <see cref="IScopeDataSource"/> contract onto
/// the connector's live <see cref="IConnectorContainerBrowser"/>. This is the thin
/// seam that lets Conduit drive the SHARED <c>ScopeTree</c> component (the same code
/// IC runs) — fixing the drift where Conduit had its own divergent scope tree.
///
/// Browsing is LIVE: each call binds the source connector's stored credentials
/// (honoring the per-project credential-name override via
/// <see cref="CredentialNameContext"/>) and enumerates one level of containers.
/// </summary>
public sealed class ConduitScopeDataSource : IScopeDataSource
{
    private readonly IConnectorContainerBrowser _browser;
    private readonly string? _credentialName;

    public ConduitScopeDataSource(IConnectorContainerBrowser browser, string? credentialName)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _credentialName = credentialName;
    }

    public async Task<ScopeLoadResult> LoadChildrenAsync(string? parentDn, CancellationToken cancellationToken)
    {
        // Honor the per-project source credential override during the live bind,
        // exactly as the old modal did.
        using var scope = CredentialNameContext.Push(_credentialName, null);

        DirectoryBrowseResult result;
        try
        {
            result = await _browser.BrowseContainersAsync(parentDn, cancellationToken);
        }
        catch (Exception ex)
        {
            return ScopeLoadResult.Fail(ex.Message);
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
            return ScopeLoadResult.Fail(result.ErrorMessage);

        var nodes = result.Nodes.Select(n => new ScopeNode
        {
            Name = n.Name,
            Dn = n.DistinguishedName,
            Description = n.Description,
            HasChildren = n.HasChildren,
            NodeType = "ou",
        }).ToList();

        return ScopeLoadResult.Ok(nodes, result.ResolvedBaseDn);
    }
}
