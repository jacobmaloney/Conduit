using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

public sealed class GwUser
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#user";
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("primaryEmail")] public string PrimaryEmail { get; set; } = string.Empty;
    [JsonProperty("name")] public GwName Name { get; set; } = new();
    [JsonProperty("isAdmin")] public bool IsAdmin { get; set; }
    [JsonProperty("isDelegatedAdmin")] public bool IsDelegatedAdmin { get; set; }
    [JsonProperty("lastLoginTime", NullValueHandling = NullValueHandling.Ignore)] public DateTime? LastLoginTime { get; set; }
    [JsonProperty("creationTime")] public DateTime CreationTime { get; set; }
    [JsonProperty("deletionTime", NullValueHandling = NullValueHandling.Ignore)] public DateTime? DeletionTime { get; set; }
    [JsonProperty("agreedToTerms")] public bool AgreedToTerms { get; set; } = true;
    [JsonProperty("suspended")] public bool Suspended { get; set; }
    [JsonProperty("suspensionReason", NullValueHandling = NullValueHandling.Ignore)] public string? SuspensionReason { get; set; }
    [JsonProperty("archived")] public bool Archived { get; set; }
    [JsonProperty("changePasswordAtNextLogin")] public bool ChangePasswordAtNextLogin { get; set; }
    [JsonProperty("ipWhitelisted")] public bool IpWhitelisted { get; set; }
    [JsonProperty("emails", NullValueHandling = NullValueHandling.Ignore)] public List<GwEmail>? Emails { get; set; }
    [JsonProperty("phones", NullValueHandling = NullValueHandling.Ignore)] public List<GwPhone>? Phones { get; set; }
    [JsonProperty("addresses", NullValueHandling = NullValueHandling.Ignore)] public List<GwAddress>? Addresses { get; set; }
    [JsonProperty("organizations", NullValueHandling = NullValueHandling.Ignore)] public List<GwOrganization>? Organizations { get; set; }
    [JsonProperty("relations", NullValueHandling = NullValueHandling.Ignore)] public List<GwRelation>? Relations { get; set; }
    [JsonProperty("websites", NullValueHandling = NullValueHandling.Ignore)] public List<GwWebsite>? Websites { get; set; }
    [JsonProperty("languages", NullValueHandling = NullValueHandling.Ignore)] public List<GwLanguage>? Languages { get; set; }
    [JsonProperty("aliases", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Aliases { get; set; }
    [JsonProperty("nonEditableAliases", NullValueHandling = NullValueHandling.Ignore)] public List<string>? NonEditableAliases { get; set; }
    [JsonProperty("customerId")] public string CustomerId { get; set; } = string.Empty;
    [JsonProperty("orgUnitPath")] public string OrgUnitPath { get; set; } = "/";
    [JsonProperty("isMailboxSetup")] public bool IsMailboxSetup { get; set; } = true;
    [JsonProperty("includeInGlobalAddressList")] public bool IncludeInGlobalAddressList { get; set; } = true;
    [JsonProperty("thumbnailPhotoUrl", NullValueHandling = NullValueHandling.Ignore)] public string? ThumbnailPhotoUrl { get; set; }
    [JsonProperty("recoveryEmail", NullValueHandling = NullValueHandling.Ignore)] public string? RecoveryEmail { get; set; }
    [JsonProperty("recoveryPhone", NullValueHandling = NullValueHandling.Ignore)] public string? RecoveryPhone { get; set; }
    [JsonProperty("customSchemas", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, Dictionary<string, object>>? CustomSchemas { get; set; }
    [JsonProperty("password", NullValueHandling = NullValueHandling.Ignore)] public string? Password { get; set; }
    [JsonProperty("hashFunction", NullValueHandling = NullValueHandling.Ignore)] public string? HashFunction { get; set; }
}

public sealed class GwName
{
    [JsonProperty("givenName")] public string GivenName { get; set; } = string.Empty;
    [JsonProperty("familyName")] public string FamilyName { get; set; } = string.Empty;
    [JsonProperty("fullName")] public string FullName { get; set; } = string.Empty;
}

public sealed class GwEmail
{
    [JsonProperty("address")] public string Address { get; set; } = string.Empty;
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("customType", NullValueHandling = NullValueHandling.Ignore)] public string? CustomType { get; set; }
    [JsonProperty("primary")] public bool Primary { get; set; }
}

public sealed class GwPhone
{
    [JsonProperty("value")] public string Value { get; set; } = string.Empty;
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("customType", NullValueHandling = NullValueHandling.Ignore)] public string? CustomType { get; set; }
    [JsonProperty("primary")] public bool Primary { get; set; }
}

public sealed class GwAddress
{
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("formatted", NullValueHandling = NullValueHandling.Ignore)] public string? Formatted { get; set; }
    [JsonProperty("streetAddress", NullValueHandling = NullValueHandling.Ignore)] public string? StreetAddress { get; set; }
    [JsonProperty("locality", NullValueHandling = NullValueHandling.Ignore)] public string? Locality { get; set; }
    [JsonProperty("region", NullValueHandling = NullValueHandling.Ignore)] public string? Region { get; set; }
    [JsonProperty("postalCode", NullValueHandling = NullValueHandling.Ignore)] public string? PostalCode { get; set; }
    [JsonProperty("country", NullValueHandling = NullValueHandling.Ignore)] public string? Country { get; set; }
    [JsonProperty("countryCode", NullValueHandling = NullValueHandling.Ignore)] public string? CountryCode { get; set; }
    [JsonProperty("primary")] public bool Primary { get; set; }
}

public sealed class GwOrganization
{
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string? Name { get; set; }
    [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] public string? Title { get; set; }
    [JsonProperty("department", NullValueHandling = NullValueHandling.Ignore)] public string? Department { get; set; }
    [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)] public string? Description { get; set; }
    [JsonProperty("costCenter", NullValueHandling = NullValueHandling.Ignore)] public string? CostCenter { get; set; }
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("primary")] public bool Primary { get; set; }
}

public sealed class GwRelation
{
    [JsonProperty("value")] public string Value { get; set; } = string.Empty;      // manager email
    [JsonProperty("type")] public string Type { get; set; } = "manager";
    [JsonProperty("customType", NullValueHandling = NullValueHandling.Ignore)] public string? CustomType { get; set; }
}

public sealed class GwWebsite
{
    [JsonProperty("value")] public string Value { get; set; } = string.Empty;
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("primary")] public bool Primary { get; set; }
}

public sealed class GwLanguage
{
    [JsonProperty("languageCode")] public string LanguageCode { get; set; } = "en";
    [JsonProperty("preference", NullValueHandling = NullValueHandling.Ignore)] public string? Preference { get; set; }
}
