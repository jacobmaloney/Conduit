using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Conduit.Connectors.EntraID;
using Conduit.Sync.Connectors;
using Conduit.Sync.Templates;
using Microsoft.Graph.Models;
using Xunit;

namespace Conduit.Connectors.EntraID.Tests;

/// <summary>
/// The collection side for service principals, applications, groups, devices and
/// OAuth2 grants: credential EXPIRY metadata (never the key blob / secretText),
/// owners, device sign-in / registration timestamps, and grant join keys. Pure
/// converter tests on Graph model POCOs — GraphServiceClient is a sealed Kiota
/// client and cannot be mocked, so the /owners fetch itself is not exercised here.
/// </summary>
public class EntraIDCollectionMetadataTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] KeyBlob = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE----- PRIVATE KEY BYTES MUST NEVER LEAVE GRAPH");
    private const string Secret = "do-not-emit-this-secret-value";

    private static ServicePrincipal FakeSp() => new()
    {
        Id = "sp-1",
        AppId = "app-1",
        DisplayName = "Billing Sync",
        ServicePrincipalType = "Application",
        AccountEnabled = true,
        PasswordCredentials = new List<PasswordCredential>
        {
            new()
            {
                KeyId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "rotated-2025",
                Hint = "do-",
                SecretText = Secret,
                StartDateTime = Now.AddDays(-400),
                EndDateTime = Now.AddDays(-35)
            },
            new()
            {
                KeyId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                DisplayName = "current",
                Hint = "xyz",
                StartDateTime = Now.AddDays(-10),
                EndDateTime = Now.AddDays(12)
            }
        },
        KeyCredentials = new List<KeyCredential>
        {
            new()
            {
                KeyId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                DisplayName = "CN=billing-cert",
                Type = "AsymmetricX509Cert",
                Usage = "Verify",
                Key = KeyBlob,
                CustomKeyIdentifier = new byte[] { 0xAB, 0xCD },
                StartDateTime = Now.AddDays(-100),
                EndDateTime = Now.AddDays(200)
            }
        }
    };

    private static string Flatten(ConnectorObject obj) =>
        string.Join("\n", obj.Attributes.Select(kv => kv.Key + "=" + (kv.Value is IEnumerable<string> l ? string.Join(";", l) : kv.Value?.ToString())));

    [Fact]
    public void ServicePrincipal_credentials_emit_expiry_metadata_and_never_key_bytes_or_secretText()
    {
        var obj = EntraIDSource.ConvertServicePrincipal(FakeSp(), owners: null, Now);
        var flat = Flatten(obj);

        Assert.DoesNotContain("secretText", flat, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, flat);
        Assert.DoesNotContain(Convert.ToBase64String(KeyBlob), flat);
        Assert.DoesNotContain("PRIVATE KEY BYTES", flat);
        Assert.DoesNotContain("\"key\":", flat, StringComparison.OrdinalIgnoreCase);
        // hint is the first characters of the live secret — IC never holds the secret, so
        // it has no consumer and is not stored.
        Assert.DoesNotContain("hint", flat, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, obj.Attributes["credentialCount"]);
        Assert.Equal(true, obj.Attributes["hasExpiredCredential"]);
        Assert.Equal(true, obj.Attributes["hasCredentialExpiringWithin30d"]);
        Assert.Equal(Now.AddDays(-35).ToString("o"), obj.Attributes["earliestCredentialExpiry"]);

        using var doc = JsonDocument.Parse((string)obj.Attributes["credentials"]!);
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        // Sorted by endDateTime: expired password, expiring password, cert.
        Assert.Equal("11111111-1111-1111-1111-111111111111", entries[0].GetProperty("keyId").GetString());
        Assert.Equal("password", entries[0].GetProperty("kind").GetString());
        Assert.Equal("key", entries[2].GetProperty("kind").GetString());
        Assert.Equal("AsymmetricX509Cert", entries[2].GetProperty("type").GetString());
        Assert.Equal("Verify", entries[2].GetProperty("usage").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xAB, 0xCD }), entries[2].GetProperty("customKeyIdentifier").GetString());
        var allowed = new HashSet<string> { "keyId", "displayName", "kind", "type", "usage", "startDateTime", "endDateTime", "customKeyIdentifier" };
        foreach (var e in entries)
            foreach (var prop in e.EnumerateObject())
                Assert.Contains(prop.Name, allowed);
    }

    [Fact]
    public void ServicePrincipal_without_credentials_emits_count_zero_and_no_credentials_attribute()
    {
        var obj = EntraIDSource.ConvertServicePrincipal(new ServicePrincipal { Id = "sp-0", DisplayName = "Gallery app" }, null, Now);
        Assert.Equal(0, obj.Attributes["credentialCount"]);
        Assert.Equal(false, obj.Attributes["hasExpiredCredential"]);
        Assert.Equal(false, obj.Attributes["hasCredentialExpiringWithin30d"]);
        Assert.False(obj.Attributes.ContainsKey("credentials"));
        Assert.False(obj.Attributes.ContainsKey("earliestCredentialExpiry"));
        Assert.False(obj.Attributes.ContainsKey("ownerCount"));
    }

    [Fact]
    public void Application_credentials_take_the_same_projection()
    {
        var app = new Application
        {
            Id = "a-1", AppId = "app-1", DisplayName = "Billing Sync (registration)",
            KeyCredentials = new List<KeyCredential> { new() { KeyId = Guid.NewGuid(), Key = KeyBlob, EndDateTime = Now.AddDays(5) } }
        };
        var obj = EntraIDSource.ConvertApplication(app, null, Now);
        var flat = Flatten(obj);
        Assert.DoesNotContain(Convert.ToBase64String(KeyBlob), flat);
        Assert.Equal(1, obj.Attributes["credentialCount"]);
        Assert.Equal(false, obj.Attributes["hasExpiredCredential"]);
        Assert.Equal(true, obj.Attributes["hasCredentialExpiringWithin30d"]);
    }

    [Fact]
    public void Owners_emit_json_count_and_join_ids_for_users_and_service_principals()
    {
        var owners = new List<DirectoryObject>
        {
            new User { Id = "u-1", DisplayName = "Ada", UserPrincipalName = "ada@contoso.com" },
            new ServicePrincipal { Id = "sp-9", DisplayName = "Automation" },
            new DirectoryObject { Id = "" }
        };
        var obj = EntraIDSource.ConvertServicePrincipal(FakeSp(), owners, Now);

        Assert.Equal(2, obj.Attributes["ownerCount"]);
        Assert.Equal(new[] { "u-1", "sp-9" }, Assert.IsAssignableFrom<IEnumerable<string>>(obj.Attributes["ownerIds"]));
        using var doc = JsonDocument.Parse((string)obj.Attributes["owners"]!);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal("ada@contoso.com", arr[0].GetProperty("upn").GetString());
        Assert.Equal("Automation", arr[1].GetProperty("displayName").GetString());
        Assert.False(arr[1].TryGetProperty("upn", out _));
    }

    [Fact]
    public void Owners_empty_list_stamps_zero_but_null_stamps_nothing()
    {
        var withEmpty = EntraIDSource.ConvertApplication(new Application { Id = "a" }, new List<DirectoryObject>(), Now);
        Assert.Equal(0, withEmpty.Attributes["ownerCount"]);
        Assert.False(withEmpty.Attributes.ContainsKey("owners"));

        var notFetched = EntraIDSource.ConvertApplication(new Application { Id = "a" }, null, Now);
        Assert.False(notFetched.Attributes.ContainsKey("ownerCount"));
    }

    [Fact]
    public void Device_emits_last_sign_in_and_registration_under_the_template_names()
    {
        var d = new Device
        {
            Id = "d-1", DisplayName = "LAPTOP-01", ManagementType = "MDM",
            ApproximateLastSignInDateTime = Now.AddDays(-3),
            RegistrationDateTime = Now.AddDays(-300)
        };
        var obj = EntraIDSource.ConvertDevice(d);
        Assert.Equal(Now.AddDays(-3).ToString("o"), obj.Attributes["lastSignInDateTime"]);
        Assert.Equal(Now.AddDays(-300).ToString("o"), obj.Attributes["createdDateTime"]);
        Assert.Equal(Now.AddDays(-300).ToString("o"), obj.Attributes["registrationDateTime"]);
        Assert.Equal("MDM", obj.Attributes["managementType"]);
    }

    [Fact]
    public void OAuth2_grant_emits_service_principal_join_keys_and_split_scopes()
    {
        var g = new OAuth2PermissionGrant
        {
            Id = "g-1", ClientId = "sp-client", ResourceId = "sp-graph", PrincipalId = "u-1",
            ConsentType = "Principal", Scope = " User.Read  Mail.Read offline_access "
        };
        var obj = EntraIDSource.ConvertOAuth2PermissionGrant(g);
        Assert.Equal("sp-client", obj.Attributes["clientServicePrincipalSourceUniqueId"]);
        Assert.Equal("sp-graph", obj.Attributes["resourceServicePrincipalSourceUniqueId"]);
        Assert.Equal("u-1", obj.Attributes["principalUserSourceUniqueId"]);
        Assert.Equal(new[] { "User.Read", "Mail.Read", "offline_access" }, Assert.IsAssignableFrom<IEnumerable<string>>(obj.Attributes["scopes"]));

        var adminGrant = EntraIDSource.ConvertOAuth2PermissionGrant(new OAuth2PermissionGrant { Id = "g-2", ClientId = "c", ResourceId = "r", ConsentType = "AllPrincipals" });
        Assert.False(adminGrant.Attributes.ContainsKey("principalUserSourceUniqueId"));
        Assert.False(adminGrant.Attributes.ContainsKey("scopes"));
    }

    // ─── closed-set template guard ───────────────────────────────────────────
    // Every attribute a converter emits must be in its (EntraID, class) template, or
    // AttributeMapResolver drops it before it reaches any sink.

    public static IEnumerable<object[]> EmittedObjects()
    {
        var owners = new List<DirectoryObject> { new User { Id = "u-1", DisplayName = "Ada", UserPrincipalName = "ada@contoso.com" } };
        yield return new object[] { "ServicePrincipal", EntraIDSource.ConvertServicePrincipal(FakeSp(), owners, Now) };
        yield return new object[] { "Application", EntraIDSource.ConvertApplication(new Application
        {
            Id = "a-1", AppId = "app", DisplayName = "App", SignInAudience = "AzureADMyOrg", PublisherDomain = "contoso.com",
            Description = "d", IdentifierUris = new List<string> { "api://x" }, Tags = new List<string> { "t" }, CreatedDateTime = Now,
            PasswordCredentials = new List<PasswordCredential> { new() { KeyId = Guid.NewGuid(), EndDateTime = Now.AddDays(1) } }
        }, owners, Now) };
        yield return new object[] { "Device", EntraIDSource.ConvertDevice(new Device
        {
            Id = "d", DisplayName = "D", DeviceId = "dev", OperatingSystem = "Windows", OperatingSystemVersion = "11",
            TrustType = "AzureAd", ManagementType = "MDM", Manufacturer = "m", Model = "x", IsManaged = true, IsCompliant = true,
            AccountEnabled = true, ApproximateLastSignInDateTime = Now, RegistrationDateTime = Now
        }) };
        yield return new object[] { "OAuth2PermissionGrant", EntraIDSource.ConvertOAuth2PermissionGrant(new OAuth2PermissionGrant
        {
            Id = "g", ClientId = "c", ResourceId = "r", PrincipalId = "p", ConsentType = "Principal", Scope = "User.Read"
        }) };
    }

    [Theory]
    [MemberData(nameof(EmittedObjects))]
    public void Every_emitted_attribute_has_a_template_entry(string objectClass, ConnectorObject obj)
    {
        var template = AttributeTemplateCatalog.Get(AttributeTemplateCatalog.Systems.EntraID, objectClass)!;
        var templated = new HashSet<string>(template.Select(e => e.SourceAttribute), StringComparer.OrdinalIgnoreCase);
        var untemplated = obj.Attributes.Keys
            .Where(k => !string.Equals(k, "objectClass", StringComparison.OrdinalIgnoreCase))
            .Where(k => !templated.Contains(k))
            .ToList();
        Assert.True(untemplated.Count == 0, $"{objectClass} emits attributes with no template entry: {string.Join(", ", untemplated)}");
    }

    [Theory]
    [InlineData("ServicePrincipal", "credentials")]
    [InlineData("ServicePrincipal", "earliestCredentialExpiry")]
    [InlineData("ServicePrincipal", "hasCredentialExpiringWithin30d")]
    [InlineData("ServicePrincipal", "owners")]
    [InlineData("Application", "credentialCount")]
    [InlineData("OAuth2PermissionGrant", "clientServicePrincipalSourceUniqueId")]
    [InlineData("OAuth2PermissionGrant", "scopes")]
    public void New_attributes_reach_the_IdentityCenter_sink_under_their_own_names(string objectClass, string attribute)
    {
        var mappings = AttributeMapResolver.Resolve(
            AttributeTemplateCatalog.Systems.EntraID, AttributeTemplateCatalog.Systems.IdentityCenter, objectClass, out var dropped);
        Assert.DoesNotContain(dropped, d => d.SourceAttribute == attribute);
        Assert.Contains(mappings, m => m.SourceAttribute == attribute && m.SinkAttribute == attribute);
    }

    [Fact]
    public void Device_sign_in_lands_on_the_promoted_LastLogonTimestamp_key()
    {
        var mappings = AttributeMapResolver.Resolve(
            AttributeTemplateCatalog.Systems.EntraID, AttributeTemplateCatalog.Systems.IdentityCenter, "Device", out var dropped);
        Assert.DoesNotContain(dropped, d => d.SourceAttribute == "lastSignInDateTime");
        Assert.Contains(mappings, m => m.SourceAttribute == "lastSignInDateTime" && m.SinkAttribute == "LastLogonTimestamp");
        Assert.Contains(mappings, m => m.SourceAttribute == "createdDateTime" && m.SinkAttribute == "WhenCreated");
    }

    [Theory]
    [InlineData("owners")]
    [InlineData("ownerCount")]
    [InlineData("ownerIds")]
    public void Entra_group_owners_survive_the_IdentityCenter_Group_inner_join(string attribute)
    {
        var mappings = AttributeMapResolver.Resolve(
            AttributeTemplateCatalog.Systems.EntraID, AttributeTemplateCatalog.Systems.IdentityCenter, "Group", out var dropped);
        Assert.DoesNotContain(dropped, d => d.SourceAttribute == attribute);
        Assert.Contains(mappings, m => m.SourceAttribute == attribute && m.SinkAttribute == attribute);

        // Sink-only on IC: an IC-sourced project must never hand the projection out.
        var reverse = AttributeMapResolver.Resolve(
            AttributeTemplateCatalog.Systems.IdentityCenter, AttributeTemplateCatalog.Systems.EntraID, "Group");
        Assert.DoesNotContain(reverse, m => m.SourceAttribute == attribute);
    }
}
