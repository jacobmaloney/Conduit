using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

public sealed class GwGroup
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#group";
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("email")] public string Email { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)] public string? Description { get; set; }
    [JsonProperty("adminCreated")] public bool AdminCreated { get; set; } = true;
    [JsonProperty("directMembersCount")] public long DirectMembersCount { get; set; }
    [JsonProperty("aliases", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Aliases { get; set; }
    [JsonProperty("nonEditableAliases", NullValueHandling = NullValueHandling.Ignore)] public List<string>? NonEditableAliases { get; set; }
}
