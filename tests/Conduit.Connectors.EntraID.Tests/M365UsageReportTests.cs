using System.Collections.Generic;
using System.Text.Json;
using Conduit.Connectors.EntraID;
using Conduit.Sync.Templates;
using Xunit;

namespace Conduit.Connectors.EntraID.Tests;

/// <summary>
/// Covers the M365-workloads ingestion additions:
///   (1) the wizard/generator advertises m365usage (EntraID) + sharepointgroup
///       (SharePoint) and the catalog routes them to templates,
///   (2) the anonymization detector (LooksConcealed),
///   (3) the report-row -> ConnectorObject attribute mapping (BuildUsageObject).
/// None require a live Graph tenant.
/// </summary>
public class M365UsageReportTests
{
    // ─── (1) dispatch / advertisement ────────────────────────────────────────

    [Fact]
    public void Generator_EntraFull_advertises_m365usage()
    {
        var generator = new SyncProjectGenerator(null!);
        var classes = generator.GetObjectClasses("EntraID", GenerationMode.Full);
        Assert.Contains("m365usage", classes);
    }

    [Fact]
    public void Generator_SharePoint_advertises_sharepointgroup()
    {
        var generator = new SyncProjectGenerator(null!);
        Assert.Contains("sharepointgroup", generator.GetObjectClasses("SharePoint", GenerationMode.Full));
        Assert.Contains("sharepointgroup", generator.GetObjectClasses("SharePoint", GenerationMode.SharePointCollaboration));
    }

    [Fact]
    public void Catalog_has_templates_for_the_new_classes()
    {
        Assert.NotNull(AttributeTemplateCatalog.Get("EntraID", "m365usage"));
        Assert.NotNull(AttributeTemplateCatalog.Get("SharePoint", "sharepointgroup"));
        // Site template now carries storage attributes.
        var site = AttributeTemplateCatalog.Get("SharePoint", "Site");
        Assert.NotNull(site);
        Assert.Contains(site!, e => e.SourceAttribute == "StorageUsedBytes");
        Assert.Contains(site!, e => e.SourceAttribute == "StorageAllocatedBytes");
    }

    [Fact]
    public void M365Usage_object_class_name_is_lowercase_native()
    {
        Assert.Equal("m365usage", M365UsageReportSource.ObjectClassName);
    }

    // ─── (2) anonymization detector ──────────────────────────────────────────

    [Fact]
    public void LooksConcealed_true_for_concealed_hash_tokens()
    {
        var concealed = new[]
        {
            "9C2179A4F2B14E0E9B5C0A3D6E7F8A1B",
            "A1B2C3D4E5F60718293A4B5C6D7E8F90",
            "0F1E2D3C4B5A69788796A5B4C3D2E1F0",
            "DEADBEEFCAFEBABE0011223344556677",
        };
        Assert.True(M365UsageReportSource.LooksConcealed(concealed));
    }

    [Fact]
    public void LooksConcealed_false_for_real_upns()
    {
        var real = new[]
        {
            "alice@contoso.com",
            "bob.smith@contoso.com",
            "carol@fabrikam.onmicrosoft.com",
            "dave@contoso.com",
        };
        Assert.False(M365UsageReportSource.LooksConcealed(real));
    }

    [Fact]
    public void LooksConcealed_false_for_empty_sample()
    {
        Assert.False(M365UsageReportSource.LooksConcealed(new string?[] { null, "", "   " }));
    }

    [Fact]
    public void LooksConcealed_false_when_majority_are_real()
    {
        var mixed = new[]
        {
            "alice@contoso.com",
            "bob@contoso.com",
            "carol@contoso.com",
            "DEADBEEFCAFEBABE0011223344556677", // one stray opaque value
        };
        Assert.False(M365UsageReportSource.LooksConcealed(mixed));
    }

    // ─── (2b) nextLink host guard (token-leak prevention) ────────────────────

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/reports/getMailboxUsageDetail?$skiptoken=abc")]
    [InlineData("https://graph.microsoft.com/v1.0/users?$top=100")]
    [InlineData("https://GRAPH.MICROSOFT.COM/v1.0/users")]                 // host compare is case-insensitive
    [InlineData("https://canary.graph.microsoft.com/v1.0/users")]          // legit subdomain
    public void IsGraphHost_true_for_graph_https(string url)
    {
        Assert.True(M365UsageReportSource.IsGraphHost(url));
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/users")]                  // not https
    [InlineData("https://evil.com/v1.0/users")]
    [InlineData("https://graph.microsoft.com.evil.com/v1.0/users")]        // suffix-spoof: must be REJECTED
    [InlineData("https://graph.microsoft.us/v1.0/users")]                  // sovereign cloud: intentionally NOT allowed
    [InlineData("https://notgraph.microsoft.com/v1.0/users")]             // missing leading dot before suffix
    [InlineData("not-a-url")]
    [InlineData("")]
    public void IsGraphHost_false_for_non_graph_or_non_https(string url)
    {
        Assert.False(M365UsageReportSource.IsGraphHost(url));
    }

    // ─── (3) report-row -> ConnectorObject mapping ───────────────────────────

    [Fact]
    public void BuildUsageObject_merges_reports_with_correct_names_and_types()
    {
        var active = Row(@"{
            ""userPrincipalName"": ""alice@contoso.com"",
            ""displayName"": ""Alice Adams"",
            ""hasExchangeLicense"": true,
            ""hasOneDriveLicense"": true,
            ""hasSharePointLicense"": false,
            ""hasTeamsLicense"": true,
            ""hasYammerLicense"": false,
            ""exchangeLastActivityDate"": ""2026-06-01"",
            ""teamsLastActivityDate"": ""2026-06-10"",
            ""assignedProducts"": ""MICROSOFT 365 E5"",
            ""reportRefreshDate"": ""2026-06-12""
        }");
        var oneDrive = Row(@"{ ""userPrincipalName"": ""alice@contoso.com"", ""storageUsedInBytes"": ""1048576"", ""storageAllocatedInBytes"": ""1099511627776"" }");
        var mailbox = Row(@"{ ""userPrincipalName"": ""alice@contoso.com"", ""storageUsedInBytes"": ""2097152"", ""prohibitSendReceiveQuotaInBytes"": ""107374182400"" }");
        var apps = Row(@"{ ""userPrincipalName"": ""alice@contoso.com"", ""lastActivityDate"": ""2026-06-09"" }");
        var teams = Row(@"{ ""userPrincipalName"": ""alice@contoso.com"", ""teamChatMessageCount"": ""42"", ""callCount"": ""3"", ""meetingCount"": ""7"" }");

        var obj = M365UsageReportSource.BuildUsageObject("alice@contoso.com", active, oneDrive, mailbox, apps, teams);

        Assert.Equal("m365usage", obj.ObjectClass);
        Assert.Equal("alice@contoso.com", obj.SourceId);
        Assert.Equal("m365usage", obj.Attributes["objectClass"]);
        Assert.Equal("alice@contoso.com", obj.Attributes["UserPrincipalName"]);
        Assert.Equal("Alice Adams", obj.Attributes["DisplayName"]);

        // bools come out as bool
        Assert.Equal(true, obj.Attributes["HasExchangeLicense"]);
        Assert.Equal(false, obj.Attributes["HasSharePointLicense"]);

        // storage longs come out as long
        Assert.Equal(1048576L, obj.Attributes["OneDriveStorageUsedBytes"]);
        Assert.Equal(1099511627776L, obj.Attributes["OneDriveStorageAllocatedBytes"]);
        Assert.Equal(2097152L, obj.Attributes["MailboxStorageUsedBytes"]);

        // teams counts long
        Assert.Equal(42L, obj.Attributes["TeamsChatMessages"]);
        Assert.Equal(3L, obj.Attributes["TeamsCallCount"]);
        Assert.Equal(7L, obj.Attributes["TeamsMeetingCount"]);

        Assert.Equal("MICROSOFT 365 E5", obj.Attributes["AssignedProducts"]);
    }

    [Fact]
    public void BuildUsageObject_omits_columns_from_403d_reports()
    {
        var active = Row(@"{ ""userPrincipalName"": ""bob@contoso.com"", ""displayName"": ""Bob"" }");

        // All side reports null (each 403'd). Object still builds from the spine.
        var obj = M365UsageReportSource.BuildUsageObject("bob@contoso.com", active, null, null, null, null);

        Assert.Equal("bob@contoso.com", obj.SourceId);
        Assert.Equal("Bob", obj.Attributes["DisplayName"]);
        Assert.False(obj.Attributes.ContainsKey("OneDriveStorageUsedBytes"));
        Assert.False(obj.Attributes.ContainsKey("TeamsChatMessages"));
    }

    private static IReadOnlyDictionary<string, JsonElement> Row(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }
}
