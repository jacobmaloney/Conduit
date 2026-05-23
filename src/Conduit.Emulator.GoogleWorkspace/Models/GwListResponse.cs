using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

public sealed class GwUsersList
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#users";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("users", NullValueHandling = NullValueHandling.Ignore)] public List<GwUser>? Users { get; set; }
    [JsonProperty("nextPageToken", NullValueHandling = NullValueHandling.Ignore)] public string? NextPageToken { get; set; }
}

public sealed class GwGroupsList
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#groups";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("groups", NullValueHandling = NullValueHandling.Ignore)] public List<GwGroup>? Groups { get; set; }
    [JsonProperty("nextPageToken", NullValueHandling = NullValueHandling.Ignore)] public string? NextPageToken { get; set; }
}

public sealed class GwMembersList
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#members";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("members", NullValueHandling = NullValueHandling.Ignore)] public List<GwMember>? Members { get; set; }
    [JsonProperty("nextPageToken", NullValueHandling = NullValueHandling.Ignore)] public string? NextPageToken { get; set; }
}

public sealed class GwDomainsList
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#domains";
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("domains", NullValueHandling = NullValueHandling.Ignore)] public List<GwDomain>? Domains { get; set; }
}
