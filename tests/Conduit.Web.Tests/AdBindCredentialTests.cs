using Conduit.Connectors.ActiveDirectory;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// How the bind credential is shaped for a SASL (Negotiate) bind to Active Directory.
///
/// Found on 2026-08-21 driving a queued disable all the way to a domain controller. Writes on :389
/// now bind with Negotiate sign+seal, because the DC refuses a simple bind over plaintext
/// ("strongerAuthRequired") and a simple bind would put the service account's password on the wire
/// anyway. But Negotiate is fussier about the credential than simple bind is, and the difference is
/// invisible: measured against the lab DC with one correct password,
///
///     NetworkCredential("Administrator", pw)                      -> BIND OK
///     NetworkCredential("domain.local\Administrator", pw)         -> "The supplied credential is invalid"
///     NetworkCredential("Administrator", pw, "domain.local")      -> BIND OK
///
/// Same account, same password, three spellings. The failure reads exactly like a wrong password,
/// which is the worst possible message for the actual cause, so the split is pinned here.
/// </summary>
public class AdBindCredentialTests
{
    [Theory]
    [InlineData(@"DOMAIN\Administrator", "Administrator", "DOMAIN")]
    [InlineData(@"domain.local\svc-sync", "svc-sync", "domain.local")]
    [InlineData(@"CONTOSO\a.user", "a.user", "CONTOSO")]
    public void A_down_level_logon_name_is_split_into_user_and_domain(string input, string user, string domain)
    {
        var (u, d) = ActiveDirectorySink.SplitDownLevelLogonName(input);
        Assert.Equal(user, u);
        Assert.Equal(domain, d);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("svc-sync@domain.local")]
    public void A_bare_name_or_a_UPN_is_left_alone(string input)
    {
        // Both already bind correctly as a single string. Splitting a UPN on its "@" would invent a
        // domain the credential never claimed, and turn a working bind into a failing one.
        var (u, d) = ActiveDirectorySink.SplitDownLevelLogonName(input);
        Assert.Equal(input, u);
        Assert.Null(d);
    }

    [Theory]
    [InlineData(@"\Administrator")]   // no domain before the separator
    [InlineData(@"DOMAIN\")]          // no user after it
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_input_is_passed_through_rather_than_guessed_at(string? input)
    {
        // Better to hand the directory exactly what the operator configured and let it reject that,
        // than to synthesise a half-credential and produce an error about something they never typed.
        var (u, d) = ActiveDirectorySink.SplitDownLevelLogonName(input);
        Assert.Equal(input ?? string.Empty, u);
        Assert.Null(d);
    }
}
