using System.Runtime.CompilerServices;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// A key that cannot be one is refused with its reason before anything is sent, and a refusal by
/// IdentityCenter is said in plain words. Jacob, 2026-11-24, with "HTTP 401: Unauthorized" on screen
/// and about 46 characters in the key box: "why can't I connect?".
/// </summary>
public class IdentityCenterKeyShapeTests
{
    private static readonly string Good = "ic_" + new string('a', 32) + new string('0', 32);

    [Fact]
    public void A_key_as_IdentityCenter_issues_it_has_no_problem()
    {
        Assert.Equal(67, Good.Length);
        Assert.Null(IdentityCenterKeyShape.Problem(Good));
    }

    [Fact]
    public void A_key_copied_in_part_is_named_with_both_lengths()
    {
        var problem = IdentityCenterKeyShape.Problem(Good.Substring(0, 46));
        Assert.NotNull(problem);
        Assert.Contains("46 characters", problem);
        Assert.Contains("67", problem);
        Assert.Contains("shown once", problem);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("some other text that was on the clipboard", "does not start with")]
    public void Text_that_is_not_a_key_is_refused_with_its_reason(string text, string expected)
    {
        Assert.Contains(expected, IdentityCenterKeyShape.Problem(text));
    }

    [Fact]
    public void A_stray_space_or_a_wrong_character_is_named()
    {
        Assert.Contains("space", IdentityCenterKeyShape.Problem(Good + " "));
        Assert.Contains("character", IdentityCenterKeyShape.Problem(Good.Substring(0, 66) + "z"));
    }

    [Fact]
    public void The_label_names_which_box_is_wrong()
    {
        Assert.StartsWith("The agent API key", IdentityCenterKeyShape.Problem("nope", "agent API key"));
    }

    [Fact]
    public void A_refusal_is_said_in_plain_words_and_other_answers_keep_their_code()
    {
        var unknown = IdentityCenterKeyShape.FailureMessage(401, "Unauthorized", "http://ic:8080");
        Assert.Contains("did not recognise this API key", unknown);
        Assert.Contains("http://ic:8080", unknown);
        Assert.DoesNotContain("HTTP 401", unknown);

        Assert.Contains("admin scope", IdentityCenterKeyShape.FailureMessage(403, "Forbidden", "http://ic:8080"));
        Assert.Equal("HTTP 500: Internal Server Error",
            IdentityCenterKeyShape.FailureMessage(500, "Internal Server Error", "http://ic:8080"));
    }

    [Theory]
    [InlineData("IdentityCenterSource.cs")]
    [InlineData("IdentityCenterSink.cs")]
    public void Both_connection_tests_check_the_key_first_and_explain_a_refusal(string file)
    {
        var code = File.ReadAllText(RepoFile(Path.Combine("src", "Conduit.Connectors.IdentityCenter", file)));
        var test = code.Substring(code.IndexOf("Task<ConnectorTestResult> TestConnectionAsync", StringComparison.Ordinal));
        var check = test.IndexOf("IdentityCenterKeyShape.Problem(creds.ApiKey)", StringComparison.Ordinal);
        var send = test.IndexOf("BuildClient(", StringComparison.Ordinal);
        Assert.True(check >= 0 && send > check, "the key is checked before a client is built");
        Assert.Contains("IdentityCenterKeyShape.FailureMessage(", test);
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
