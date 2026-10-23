using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// UC014-D08 (deprovisioning, slice 3): "each op returns the prior value". A deprovisioning that clears a
/// phone number or hides a mailbox must know what was there, or Undo cannot put it back, and IdentityCenter's
/// catalog rarely holds those attributes. SetAttributes reads every attribute it is about to replace FIRST
/// and returns them as {"priorValues":{...}} on the command result. No LDAP in a unit test, so the order and
/// the result shape are pinned by source; the read itself is a base-scope search on the GUID-resolved DN.
/// </summary>
public class AgentPriorValuesTests
{
    private static readonly string Executor = Read("src/Conduit.Web/Services/AdAgentWriteExecutor.cs").Replace("\r\n", "\n");
    private static readonly string Sink = Read("src/Conduit.Connectors.ActiveDirectory/ActiveDirectorySink.cs").Replace("\r\n", "\n");

    [Fact]
    public void SetAttributes_reads_the_prior_values_before_its_first_write_and_returns_them()
    {
        var method = Executor[Executor.IndexOf("private async Task<(bool, string)> DoSetAttributesAsync(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("// ── SetManager", StringComparison.Ordinal)];
        var read = method.IndexOf("await sink.ReadAttributesByGuidAsync(", StringComparison.Ordinal);
        var firstWrite = method.IndexOf("await sink.SetAttributeByGuidAsync(", StringComparison.Ordinal);
        Assert.True(read > 0 && firstWrite > read, "the prior values are read before the first write");
        Assert.Contains("LastResultJson = JsonSerializer.Serialize(new { priorValues = prior });", method);
    }

    [Fact]
    public void The_prior_value_read_never_fails_the_write()
    {
        var method = Sink[Sink.IndexOf("public async Task<IReadOnlyDictionary<string, string?>?> ReadAttributesByGuidAsync(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("    /// <summary>", StringComparison.Ordinal)];
        Assert.Contains("SearchScope.Base", method);
        Assert.Contains("catch (Exception ex)", method);
        Assert.Contains("return null;", method);
    }

    [Fact]
    public void Hide_from_address_book_is_its_own_allow_listed_attribute()
    {
        Assert.Contains("[\"msExchHideFromAddressLists\"] = \"msExchHideFromAddressLists\",", Executor);
    }

    private static string Read(string relative, [CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relative.Replace('/', Path.DirectorySeparatorChar))));
}
