using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

// Produces the Google-shaped service_account.json file clients feed to
// GoogleCredential.FromStream(...). Matches real field names exactly.
public static class ServiceAccountKeyFormatter
{
    public static string ToGoogleCredentialJson(ServiceAccountRecord record, string tokenUri)
    {
        var shape = new ServiceAccountKey
        {
            Type = "service_account",
            ProjectId = record.ProjectId,
            PrivateKeyId = record.PrivateKeyId,
            PrivateKey = record.PrivateKeyPem,
            ClientEmail = record.ClientEmail,
            ClientId = record.ClientId,
            AuthUri = "https://accounts.google.com/o/oauth2/auth",
            TokenUri = tokenUri,
            AuthProviderX509CertUrl = "https://www.googleapis.com/oauth2/v1/certs",
            ClientX509CertUrl = $"https://www.googleapis.com/robot/v1/metadata/x509/{Uri.EscapeDataString(record.ClientEmail)}",
            UniverseDomain = "googleapis.com"
        };
        return JsonConvert.SerializeObject(shape, Formatting.Indented);
    }

    private sealed class ServiceAccountKey
    {
        [JsonProperty("type")] public string Type { get; set; } = "service_account";
        [JsonProperty("project_id")] public string ProjectId { get; set; } = string.Empty;
        [JsonProperty("private_key_id")] public string PrivateKeyId { get; set; } = string.Empty;
        [JsonProperty("private_key")] public string PrivateKey { get; set; } = string.Empty;
        [JsonProperty("client_email")] public string ClientEmail { get; set; } = string.Empty;
        [JsonProperty("client_id")] public string ClientId { get; set; } = string.Empty;
        [JsonProperty("auth_uri")] public string AuthUri { get; set; } = string.Empty;
        [JsonProperty("token_uri")] public string TokenUri { get; set; } = string.Empty;
        [JsonProperty("auth_provider_x509_cert_url")] public string AuthProviderX509CertUrl { get; set; } = string.Empty;
        [JsonProperty("client_x509_cert_url")] public string ClientX509CertUrl { get; set; } = string.Empty;
        [JsonProperty("universe_domain")] public string UniverseDomain { get; set; } = "googleapis.com";
    }
}
