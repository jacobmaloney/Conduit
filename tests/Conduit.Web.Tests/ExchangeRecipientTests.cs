using System;
using System.IO;
using System.Runtime.CompilerServices;
using Conduit.Connectors.ActiveDirectory;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// EXCHANGE-RECIPIENTS-01 (IdentityCenter UC017-S05/S07/S12; Jacob 2026-11-14 chose Microsoft's cmdlets over hand-set
/// msExch* values). The plan checks the operation against the AD-read class and the inputs' shapes before anything runs;
/// the runner's script reads its inputs as data only; a cmdlet's own refusal comes back as its words.
/// </summary>
public class ExchangeRecipientTests
{
    private const string Guid1 = "0786a4f0-5555-4555-8555-000000000011";

    private static AdResolvedObject User() => new("CN=Ana Rios,OU=Contractors,DC=corp,DC=local", new[] { "top", "person", "organizationalPerson", "user" }, "arios", "Ana Rios", null);
    private static AdResolvedObject Group() => new("CN=Field Ops,OU=Groups,DC=corp,DC=local", new[] { "top", "group" }, "Field Ops", "Field Ops", null);
    private static AdResolvedObject Computer() => new("CN=Server99,OU=Computers,DC=corp,DC=local", new[] { "top", "person", "organizationalPerson", "user", "computer" }, "SERVER99$", "Server99", null);

    [Fact]
    public void A_remote_mailbox_is_planned_on_a_user_with_its_routing_address_and_refused_without_one()
    {
        var (request, refusal) = ExchangeRecipientPlan.Build(ExchangeRecipientOperations.EnableRemoteMailbox, User(), Guid1,
            " arios ", "ana.rios@gcinc.com", "arios@gcinc.mail.onmicrosoft.com");
        Assert.Null(refusal);
        Assert.Equal(new ExchangeRecipientRequest(ExchangeRecipientOperations.EnableRemoteMailbox, Guid1, "arios", "ana.rios@gcinc.com", "arios@gcinc.mail.onmicrosoft.com"), request);

        Assert.Contains("routing address", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.EnableRemoteMailbox, User(), Guid1, null, null, null).Refusal);
        Assert.Contains("not a routing address", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.EnableRemoteMailbox, User(), Guid1, null, null, "not an address").Refusal);
    }

    [Fact]
    public void Each_operation_is_refused_on_the_wrong_class_by_the_AD_read_class()
    {
        Assert.Contains("only a group", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.MailEnableGroup, User(), Guid1, null, null, null).Refusal);
        Assert.Contains("user account", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.ConvertRemoteMailboxToShared, Group(), Guid1, null, null, null).Refusal);
        Assert.Contains("user account", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.EnableRemoteMailbox, Computer(), Guid1, null, null, "x@t.mail.onmicrosoft.com").Refusal);
        Assert.Contains("not an Exchange recipient operation", ExchangeRecipientPlan.Build("SetAttributes", User(), Guid1, null, null, null).Refusal);
        Assert.Contains("GUID", ExchangeRecipientPlan.Build(ExchangeRecipientOperations.MailEnableGroup, Group(), "", null, null, null).Refusal);
    }

    [Theory]
    [InlineData("a; Remove-Mailbox x", null)]
    [InlineData("$(whoami)", null)]
    [InlineData("-alias", null)]
    [InlineData(null, "ana@")]
    [InlineData(null, "ana rios@gcinc.com")]
    public void An_alias_or_address_outside_its_shape_is_refused_before_anything_runs(string? alias, string? smtp)
    {
        var (request, refusal) = ExchangeRecipientPlan.Build(ExchangeRecipientOperations.MailEnableGroup, Group(), Guid1, alias, smtp, null);
        Assert.Null(request);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Converting_to_shared_carries_only_the_object_and_mail_enabling_a_group_carries_no_routing_address()
    {
        var shared = ExchangeRecipientPlan.Build(ExchangeRecipientOperations.ConvertRemoteMailboxToShared, User(), Guid1, "arios", "a@b.com", "r@t.mail.onmicrosoft.com").Request!;
        Assert.Equal((null, null, null), (shared.Alias, shared.PrimarySmtpAddress, shared.RemoteRoutingAddress));

        var group = ExchangeRecipientPlan.Build(ExchangeRecipientOperations.MailEnableGroup, Group(), Guid1, "fieldops", "fieldops@gcinc.com", "r@t.mail.onmicrosoft.com").Request!;
        Assert.Equal(("fieldops", "fieldops@gcinc.com", (string?)null), (group.Alias, group.PrimarySmtpAddress, group.RemoteRoutingAddress));
    }

    [Fact]
    public void The_runner_reads_the_cmdlets_answer_and_returns_its_refusal_in_its_own_words()
    {
        var ok = PowerShellExchangeRecipientRunner.Parse("WARNING: something\n{\"ok\":true,\"recipientTypeDetails\":\"RemoteUserMailbox\",\"primarySmtpAddress\":\"ana.rios@gcinc.com\"}\n", "");
        Assert.Equal(new ExchangeRecipientResult(true, "RemoteUserMailbox", "ana.rios@gcinc.com", null), ok);

        var refused = PowerShellExchangeRecipientRunner.Parse("{\"ok\":false,\"error\":\"Only universal groups can be mail-enabled.\"}", "");
        Assert.Equal((false, "Only universal groups can be mail-enabled."), (refused.Ok, refused.Error));

        var nothing = PowerShellExchangeRecipientRunner.Parse("", "No snap-ins have been registered for Windows PowerShell version 5.");
        Assert.False(nothing.Ok);
        Assert.Contains("No snap-ins", nothing.Error);
    }

    [Fact]
    public void The_script_takes_its_inputs_as_data_from_standard_input_and_calls_only_the_three_cmdlets()
    {
        var script = PowerShellExchangeRecipientRunner.Script;
        Assert.Contains("[Console]::In.ReadToEnd() | ConvertFrom-Json", script);
        Assert.Contains("Add-PSSnapin Microsoft.Exchange.Management.PowerShell.RecipientManagement", script);
        Assert.Contains("Enable-RemoteMailbox @p", script);
        Assert.Contains("Set-RemoteMailbox -Identity ([string]$in.objectGuid) -Type Shared", script);
        Assert.Contains("Enable-DistributionGroup @p", script);
        // Nothing the request holds is ever evaluated as script.
        Assert.DoesNotContain("Invoke-Expression", script);
        Assert.DoesNotContain("iex ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ScriptBlock", script);
    }

    private static string Src(string relative, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_executor_allows_the_three_operations_and_guards_privileged_groups()
    {
        var executor = File.ReadAllText(Src("src/Conduit.Web/Services/AdAgentWriteExecutor.cs"));
        Assert.Contains("ExchangeRecipientOperations.EnableRemoteMailbox, ExchangeRecipientOperations.ConvertRemoteMailboxToShared", executor);
        Assert.Contains("_ when ExchangeRecipientOperations.All.Contains(operation) => await DoExchangeRecipientAsync(operation, target, p, ct)", executor);
        Assert.Contains("is a privileged group.", executor);
        var program = File.ReadAllText(Src("src/Conduit.Web/Program.cs"));
        Assert.Contains("AddSingleton<Conduit.Web.Services.IExchangeRecipientRunner, Conduit.Web.Services.PowerShellExchangeRecipientRunner>()", program);
    }
}
