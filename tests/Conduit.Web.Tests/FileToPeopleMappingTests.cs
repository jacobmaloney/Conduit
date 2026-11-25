using System.Runtime.CompilerServices;
using Conduit.Core.Models;
using Conduit.Mapping;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// A file of people going to IdentityCenter is mapped from the file's own header and the person fields
/// IdentityCenter accepts. Jacob, 2026-11-24, made an HR project in the Conduit from a real file: the
/// standard set mapped columns the file did not have (ManagerId, UserPrincipalName, PhoneNumber), left
/// out ones it did (StartDate, EndDate, City), and offered no person type at all.
/// </summary>
public class FileToPeopleMappingTests
{
    private static readonly string[] GraniteHeader =
        "EmployeeId,FirstName,LastName,DisplayName,Email,Username,Department,JobTitle,EmployeeType,ManagerEmployeeId,Company,Division,Office,City,State,PostalCode,Country,StartDate,EndDate,CostCenter".Split(',');

    // The person fields the people API accepts, as it serves them to the Conduit.
    private static readonly string[] PersonFields =
    {
        "DisplayName", "FirstName", "LastName", "PrimaryEmail", "MobilePhone", "Department", "JobTitle", "EmployeeId",
        "EmployeeType", "IdentityType", "Division", "Company", "Office", "CostCenter", "City", "State", "PostalCode",
        "Country", "Username", "UserPrincipalName", "Status", "ManagerEmployeeId", "HireDate", "TerminationDate",
        "StartDate", "EndDate",
    };

    [Fact]
    public void Every_column_of_the_real_file_is_mapped_to_the_field_it_names()
    {
        var result = FileToPeopleMapping.Build(GraniteHeader, PersonFields);

        Assert.Empty(result.Unmapped);
        string Target(string column) => result.Mappings.First(m => m.SourceAttribute == column).SinkAttribute;
        Assert.Equal("PrimaryEmail", Target("Email"));
        Assert.Equal("ManagerEmployeeId", Target("ManagerEmployeeId"));
        Assert.Equal("StartDate", Target("StartDate"));
        Assert.Equal("EndDate", Target("EndDate"));
        Assert.Equal("City", Target("City"));
        Assert.DoesNotContain(result.Mappings, m => m.SourceAttribute is "ManagerId" or "UserPrincipalName" or "PhoneNumber");
        Assert.True(result.Mappings.Single(m => m.SinkAttribute == "EmployeeId").IsRequired);
        Assert.Single(result.Mappings, m => m.IsRequired);
    }

    [Fact]
    public void The_type_column_fills_both_the_plain_attribute_and_the_type_people_are_classified_by()
    {
        var result = FileToPeopleMapping.Build(GraniteHeader, PersonFields);
        var fromType = result.Mappings.Where(m => m.SourceAttribute == "EmployeeType").Select(m => m.SinkAttribute).ToArray();
        Assert.Equal(new[] { "EmployeeType", "IdentityType" }, fromType);
        Assert.Equal(21, result.Mappings.Count);
        Assert.Equal("Mapped 20 of 20 columns from the file's header.", FileToPeopleMapping.Summary(result));
    }

    [Fact]
    public void A_file_with_its_own_type_column_is_not_given_a_second_one_and_a_target_that_refuses_the_field_gets_no_row()
    {
        var own = ColumnTargetProposal.Propose(new[] { "EmployeeType", "IdentityType" }, PersonFields,
            ColumnTargetProposal.PersonAliases, ColumnTargetProposal.PersonAlsoFills);
        Assert.Equal(new[] { "EmployeeType>EmployeeType", "IdentityType>IdentityType" }, own.Rows.Select(r => r.Column + ">" + r.Target));

        var refused = ColumnTargetProposal.Propose(new[] { "EmployeeType" }, new[] { "EmployeeType" },
            ColumnTargetProposal.PersonAliases, ColumnTargetProposal.PersonAlsoFills);
        Assert.Single(refused.Rows);
    }

    [Fact]
    public void A_column_no_field_matches_and_a_second_column_for_one_field_are_reported_not_guessed()
    {
        var result = FileToPeopleMapping.Build(new[] { "employee_id", "Shoe Size", "E-mail", "Email", " " }, PersonFields);
        Assert.Equal(new[] { "employee_id", "E-mail" }, result.Mappings.Select(m => m.SourceAttribute));
        Assert.Equal(new[] { "Shoe Size", "Email" }, result.Unmapped);
        Assert.Equal("Mapped 2 of 5 columns from the file's header. Not mapped: Shoe Size, Email.", FileToPeopleMapping.Summary(result));
        // An alias applies only when the target accepts the field it points at.
        Assert.Empty(ColumnTargetProposal.Propose(new[] { "dob" }, PersonFields, ColumnTargetProposal.PersonAliases).Rows);
        Assert.Empty(ColumnTargetProposal.Propose(new[] { "ManagerEmployeeId" }, new[] { "EmployeeId" }).Rows);
    }

    [Fact]
    public void It_applies_to_a_file_writing_people_and_to_nothing_else()
    {
        var file = new Tenant { SystemType = "CSV" };
        var ic = new Tenant { SystemType = "IdentityCenter" };
        var ad = new Tenant { SystemType = "ActiveDirectory" };
        Assert.True(FileToPeopleMapping.Applies(file, ic, "Identities"));
        Assert.False(FileToPeopleMapping.Applies(file, ic, "Objects"));
        Assert.False(FileToPeopleMapping.Applies(file, ic, null));
        Assert.False(FileToPeopleMapping.Applies(ad, ic, "Identities"));
        Assert.False(FileToPeopleMapping.Applies(file, ad, "Identities"));
        Assert.False(FileToPeopleMapping.Applies(null, ic, "Identities"));
    }

    [Fact]
    public void The_project_screen_uses_it_when_generating_when_auto_filling_and_for_suggestions()
    {
        var page = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Pages", "Sync", "SyncProjects.razor")));
        Assert.Contains("@inject Conduit.Web.Services.FileToPeopleMapping PeopleMapping", page);
        Assert.Contains("peopleProposal = await PeopleMapping.ProposeAsync(src, sink, null, CancellationToken.None);", page);
        Assert.Contains("? FreshPeopleMappings(peopleProposal)", page);
        Assert.Contains("var proposal = await LoadPeopleProposalAsync(refresh: true);", page);
        Assert.Contains("CurrentPeopleProposal() is { } people ? people.Targets :", page);
        // A failed read is said, never silent: the standard set stands and the operator is told to check it.
        Assert.Contains("so the mappings are the standard set: check them against your file.", page);
        Assert.Contains("so there is nothing to map from. Test both connections, then try again.", page);

        var program = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Web", "Program.cs")));
        Assert.Equal(2, program.Split("Conduit.Web.Services.FileToPeopleMapping").Length);

        var adapter = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Connectors.Csv", "CsvAdapter.cs")));
        Assert.Contains("public Task<IReadOnlyList<string>> ReadHeaderAsync(Guid tenantId, CancellationToken ct)", adapter);
        Assert.Contains("if (!creds.HasHeader) return Array.Empty<string>();", adapter);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
