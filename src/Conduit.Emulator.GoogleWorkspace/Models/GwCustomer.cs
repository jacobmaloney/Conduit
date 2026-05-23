using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

public sealed class GwCustomer
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#customer";
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("customerDomain")] public string CustomerDomain { get; set; } = string.Empty;
    [JsonProperty("customerCreationTime")] public DateTime CustomerCreationTime { get; set; }
    [JsonProperty("alternateEmail", NullValueHandling = NullValueHandling.Ignore)] public string? AlternateEmail { get; set; }
    [JsonProperty("phoneNumber", NullValueHandling = NullValueHandling.Ignore)] public string? PhoneNumber { get; set; }
    [JsonProperty("language")] public string Language { get; set; } = "en";
    [JsonProperty("postalAddress", NullValueHandling = NullValueHandling.Ignore)] public GwPostalAddress? PostalAddress { get; set; }
}

public sealed class GwPostalAddress
{
    [JsonProperty("organizationName", NullValueHandling = NullValueHandling.Ignore)] public string? OrganizationName { get; set; }
    [JsonProperty("contactName", NullValueHandling = NullValueHandling.Ignore)] public string? ContactName { get; set; }
    [JsonProperty("addressLine1", NullValueHandling = NullValueHandling.Ignore)] public string? AddressLine1 { get; set; }
    [JsonProperty("addressLine2", NullValueHandling = NullValueHandling.Ignore)] public string? AddressLine2 { get; set; }
    [JsonProperty("locality", NullValueHandling = NullValueHandling.Ignore)] public string? Locality { get; set; }
    [JsonProperty("region", NullValueHandling = NullValueHandling.Ignore)] public string? Region { get; set; }
    [JsonProperty("postalCode", NullValueHandling = NullValueHandling.Ignore)] public string? PostalCode { get; set; }
    [JsonProperty("countryCode", NullValueHandling = NullValueHandling.Ignore)] public string? CountryCode { get; set; }
}

public sealed class GwDomain
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#domain";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("domainName")] public string DomainName { get; set; } = string.Empty;
    [JsonProperty("isPrimary")] public bool IsPrimary { get; set; }
    [JsonProperty("verified")] public bool Verified { get; set; } = true;
    [JsonProperty("creationTime")] public long CreationTime { get; set; }
    [JsonProperty("domainAliases", NullValueHandling = NullValueHandling.Ignore)] public List<GwDomainAlias>? DomainAliases { get; set; }
}

public sealed class GwDomainAlias
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#domainAlias";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("domainAliasName")] public string DomainAliasName { get; set; } = string.Empty;
    [JsonProperty("parentDomainName")] public string ParentDomainName { get; set; } = string.Empty;
    [JsonProperty("verified")] public bool Verified { get; set; } = true;
    [JsonProperty("creationTime")] public long CreationTime { get; set; }
}
