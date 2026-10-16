using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Jacob, 2026-10-16, on the New Connection wizard: "what is this authoritative button even there
/// for? and should we even have a target table in the sync connection for identity center then?"
/// The disabled Authoritative toggle (no column, no reader) is gone. The IdentityCenter target table
/// no longer gates the wizard: sync projects choose the table per side, a blank value means inbound
/// writes land in Objects, and the Endpoints dialog is the one place to change that.
/// </summary>
public class ConnectionWizardTests
{
    private static string Page() => File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "ConnectedSystems.razor")));

    [Fact]
    public void The_wizard_has_no_authoritative_toggle_and_no_target_table_question()
    {
        var src = Page();
        Assert.DoesNotContain("connAuthoritative", src);
        Assert.DoesNotContain("connTargetTable", src);
        Assert.Contains("private bool IcTargetTableSatisfied => true;", src);
    }

    [Fact]
    public void The_endpoints_dialog_owns_the_inbound_table()
    {
        var src = Page();
        Assert.Contains("id=\"endpointsInboundTable\"", src);
        Assert.Contains("@onchange=\"OnInboundTableChanged\"", src);
        var handler = src.IndexOf("private async Task OnInboundTableChanged(ChangeEventArgs e)", StringComparison.Ordinal);
        Assert.True(handler >= 0);
        var body = src.Substring(handler);
        Assert.Contains("await TenantRepo.UpdateAsync(tenant);", body);
        Assert.Contains("could not be saved", body);
    }

    [Fact]
    public void Inbound_writes_default_to_objects_when_no_table_is_set()
    {
        var proxy = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Controllers", "ProvisioningController.cs")));
        Assert.Contains("? \"Identities\"", proxy);
        Assert.Contains(": \"Objects\";", proxy);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
