namespace Conduit.SourceBrowsing;

public sealed record SourceCatalogItem(Guid Id, string Kind, string Name, Guid? SourceConnectionId,
    string ConnectionType, string SourceName, bool Enabled, bool CanBrowse, string? Reason = null);
public sealed record SourceCatalog(string ExecutionLocation, Guid? InstanceId, string? PortalUrl, IReadOnlyList<SourceCatalogItem> Items);
public sealed record SourceBrowseFailure(string Code, string Message);

public static class SourceCatalogLinks
{
    public static string? PortalBrowseUrl(string? portalUrl, SourceCatalogItem item)
    {
        if (!Uri.TryCreate(portalUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            item.Kind is not ("Connection" or "Project")) return null;
        var connection = item.SourceConnectionId ?? (item.Kind == "Connection" ? item.Id : Guid.Empty);
        if (connection == Guid.Empty) return null;
        return $"{portalUrl!.TrimEnd('/')}/admin/system-center/{(item.Kind == "Project" ? "projects" : "connections")}?browseConnection={connection:D}" +
            (item.Kind == "Project" ? $"&browseProject={item.Id:D}" : "");
    }
}
