using Conduit.Connectors.Aws;
using Xunit;

namespace Conduit.Connectors.Cloud.Tests;

/// <summary>
/// Pure, no-AWS-account tests for the connector-side validators and ARN classifiers in
/// <see cref="AwsIamWriter"/>. These are the server-side backstop the executor re-checks:
/// IAM name + access-key-id charset, managed-policy ARN shape, customer-managed detection.
/// All four methods are public static — no SDK client is constructed.
/// </summary>
public class AwsIamWriterValidationTests
{
    // ── IsValidIamName ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("alice")]
    [InlineData("svc.account-1")]
    [InlineData("role+test=ok")]
    [InlineData("user@example")]
    [InlineData("a")]
    public void IsValidIamName_accepts_valid_names(string name)
        => Assert.True(AwsIamWriter.IsValidIamName(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("slash/here")]
    [InlineData("paren(s)")]
    public void IsValidIamName_rejects_garbage(string? name)
        => Assert.False(AwsIamWriter.IsValidIamName(name));

    [Fact]
    public void IsValidIamName_rejects_oversize()
        => Assert.False(AwsIamWriter.IsValidIamName(new string('a', 129)));

    [Fact]
    public void IsValidIamName_accepts_max_length()
        => Assert.True(AwsIamWriter.IsValidIamName(new string('a', 128)));

    // ── IsValidAccessKeyId (^[A-Z0-9]{16,128}$) ────────────────────────────────

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]   // 20 chars, classic AKIA
    [InlineData("ASIAY34FZKBOKMUTVV7A")]   // STS temporary key id
    [InlineData("ABCDEFGHIJKLMNOP")]       // exactly 16
    public void IsValidAccessKeyId_accepts_valid(string id)
        => Assert.True(AwsIamWriter.IsValidAccessKeyId(id));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("akiaiosfodnn7example")]   // lowercase
    [InlineData("AKIA OSFODNN7EXAMPL")]    // space
    [InlineData("AKIA-IOSFODNN7EXAM")]     // hyphen
    [InlineData("SHORT123")]               // < 16
    public void IsValidAccessKeyId_rejects_garbage(string? id)
        => Assert.False(AwsIamWriter.IsValidAccessKeyId(id));

    [Fact]
    public void IsValidAccessKeyId_rejects_oversize()
        => Assert.False(AwsIamWriter.IsValidAccessKeyId(new string('A', 129)));

    // ── IsValidManagedPolicyArn ────────────────────────────────────────────────

    [Theory]
    [InlineData("arn:aws:iam::123456789012:policy/MyTeamPolicy")]         // customer-managed
    [InlineData("arn:aws:iam::123456789012:policy/path/to/MyPolicy")]     // customer-managed, with path
    public void IsValidManagedPolicyArn_accepts_customer_managed_policy_arns(string arn)
        => Assert.True(AwsIamWriter.IsValidManagedPolicyArn(arn));

    // ── REGRESSION TRIPWIRE: AWS-managed ARNs must stay VALID ───────────────────
    // History: IsValidManagedPolicyArn once treated the AWS-managed account segment
    // (arn:aws:iam::aws:policy/...) as needing to be EMPTY. AWSSDK's Arn.TryParse populates
    // AccountId with the literal "aws", so the guard returned FALSE and EVERY AWS-managed
    // policy — incl. AdministratorAccess / PowerUserAccess / IAMFullAccess — was rejected,
    // killing the whole AWS-managed attach/detach path. The fix (AwsIamWriter.cs:90-92) now
    // accepts AccountId=="aws" alongside empty / 12-digit. This Theory PINS that corrected
    // behavior: it flips RED the moment anyone reintroduces the "aws"-account rejection.
    // Coverage spans both an admin ARN and a benign one so the validity verdict is proven
    // independent of privilege.
    [Theory]
    [InlineData("arn:aws:iam::aws:policy/AdministratorAccess")]   // AWS-managed, named-admin
    [InlineData("arn:aws:iam::aws:policy/PowerUserAccess")]       // AWS-managed, named-admin
    [InlineData("arn:aws:iam::aws:policy/IAMFullAccess")]         // AWS-managed, named-admin
    [InlineData("arn:aws:iam::aws:policy/ReadOnlyAccess")]        // AWS-managed, benign
    public void IsValidManagedPolicyArn_accepts_aws_managed_arns(string arn)
        => Assert.True(AwsIamWriter.IsValidManagedPolicyArn(arn),
            "REGRESSION TRIPWIRE: AwsIamWriter must accept AWS-managed (account=='aws') ARNs. " +
            "If this fails, the 'aws'-account acceptance at AwsIamWriter.cs:90-92 was reverted.");

    // None of those AWS-managed ARNs are CUSTOMER-managed (account segment is "aws", not a
    // 12-digit account). Privilege among them is decided by the named-admin set, NOT by
    // IsCustomerManagedPolicyArn — proven here so the validity fix can't smuggle an AWS-managed
    // ARN into the privileged-by-default customer-managed bucket.
    [Theory]
    [InlineData("arn:aws:iam::aws:policy/AdministratorAccess")]
    [InlineData("arn:aws:iam::aws:policy/PowerUserAccess")]
    [InlineData("arn:aws:iam::aws:policy/IAMFullAccess")]
    [InlineData("arn:aws:iam::aws:policy/ReadOnlyAccess")]
    public void Aws_managed_arns_are_never_customer_managed(string arn)
        => Assert.False(AwsIamWriter.IsCustomerManagedPolicyArn(arn));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-an-arn")]
    [InlineData("arn:aws:s3:::my-bucket")]                                // wrong service
    [InlineData("arn:aws:iam::123456789012:user/alice")]                 // not a policy resource
    [InlineData("arn:aws:iam::123456789012:role/MyRole")]                // role, not policy
    [InlineData("arn:aws-cn:iam::123456789012:policy/MyPolicy")]         // wrong partition (China)
    [InlineData("arn:aws-us-gov:iam::123456789012:policy/MyPolicy")]     // wrong partition (GovCloud)
    [InlineData("arn:aws:iam::12345:policy/MyPolicy")]                   // malformed account (not 12 digits)
    public void IsValidManagedPolicyArn_rejects_nonpolicy_and_garbage(string? arn)
        => Assert.False(AwsIamWriter.IsValidManagedPolicyArn(arn));

    // ── IsCustomerManagedPolicyArn (privileged-by-default classifier) ──────────

    [Theory]
    [InlineData("arn:aws:iam::123456789012:policy/MyTeamPolicy")]
    [InlineData("arn:aws:iam::000000000000:policy/Other")]
    public void IsCustomerManagedPolicyArn_true_for_account_scoped(string arn)
        => Assert.True(AwsIamWriter.IsCustomerManagedPolicyArn(arn));

    [Theory]
    [InlineData("arn:aws:iam::aws:policy/AdministratorAccess")]   // AWS-managed (empty account segment)
    [InlineData("arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess")]
    [InlineData("not-an-arn")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCustomerManagedPolicyArn_false_for_aws_managed_and_garbage(string? arn)
        => Assert.False(AwsIamWriter.IsCustomerManagedPolicyArn(arn));
}
