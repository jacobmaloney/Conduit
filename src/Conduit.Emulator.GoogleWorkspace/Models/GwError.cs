using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Models;

// Google Admin SDK error envelope — byte-matched to the real API.
public sealed class GwErrorEnvelope
{
    [JsonProperty("error")] public GwErrorBody Error { get; set; } = new();
}

public sealed class GwErrorBody
{
    [JsonProperty("code")] public int Code { get; set; }
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
    [JsonProperty("errors", NullValueHandling = NullValueHandling.Ignore)] public List<GwErrorDetail>? Errors { get; set; }
    [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)] public string? Status { get; set; }
}

public sealed class GwErrorDetail
{
    [JsonProperty("domain")] public string Domain { get; set; } = "global";
    [JsonProperty("reason")] public string Reason { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
    [JsonProperty("location", NullValueHandling = NullValueHandling.Ignore)] public string? Location { get; set; }
    [JsonProperty("locationType", NullValueHandling = NullValueHandling.Ignore)] public string? LocationType { get; set; }
}

// OAuth2 token endpoint error (RFC 6749) — distinct shape from Directory API.
public sealed class OAuth2Error
{
    [JsonProperty("error")] public string Error { get; set; } = string.Empty;                       // invalid_grant, invalid_client, invalid_scope…
    [JsonProperty("error_description", NullValueHandling = NullValueHandling.Ignore)] public string? ErrorDescription { get; set; }
    [JsonProperty("error_uri", NullValueHandling = NullValueHandling.Ignore)] public string? ErrorUri { get; set; }
}
