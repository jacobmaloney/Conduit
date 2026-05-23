using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

public sealed class GwMember
{
    [JsonProperty("kind")] public string Kind { get; set; } = "admin#directory#member";
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("etag")] public string? Etag { get; set; }
    [JsonProperty("email")] public string Email { get; set; } = string.Empty;
    [JsonProperty("role")] public string Role { get; set; } = "MEMBER";           // OWNER | MANAGER | MEMBER
    [JsonProperty("type")] public string Type { get; set; } = "USER";             // USER | GROUP | EXTERNAL | CUSTOMER
    [JsonProperty("status")] public string Status { get; set; } = "ACTIVE";
    [JsonProperty("delivery_settings")] public string DeliverySettings { get; set; } = "ALL_MAIL";
}
