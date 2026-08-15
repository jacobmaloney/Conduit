using Xunit;
using System.Runtime.CompilerServices;

namespace Conduit.Web.Tests;

public class FullSyncMenuTests
{
    [Fact]
    public void Full_sync_action_is_confirmed_and_supports_one_class_or_all_classes()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine(
            "src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));

        Assert.Contains("> Full Sync", page);
        Assert.Contains("All object classes", page);
        Assert.Contains("FullSyncObjectClasses", page);
        Assert.Contains("ResetForFullSyncAsync", page);
        Assert.Contains("SetRunningAsync(projectId, Guid.Empty)", page);
    }

    [Fact]
    public void Full_sync_reset_is_atomic_and_scoped_by_project_sink_and_optional_class()
    {
        var repo = File.ReadAllText(RepoFile(Path.Combine(
            "src", "Conduit.DataAccess", "Repositories", "SyncProjectRepository.cs")));

        Assert.Contains("ResetForFullSyncAsync", repo);
        Assert.Contains("s.IncrementalCursor = NULL", repo);
        Assert.Contains("DELETE FROM SinkRecordHashes", repo);
        Assert.Contains("SinkTenantId = @SinkTenantId", repo);
        Assert.Contains("@ObjectClass IS NULL OR ObjectClass = @ObjectClass", repo);
        Assert.Contains("using var tx = conn.BeginTransaction()", repo);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
