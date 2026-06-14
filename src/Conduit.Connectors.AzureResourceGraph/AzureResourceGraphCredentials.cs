using System;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Security;

namespace Conduit.Connectors.AzureResourceGraph;

/// <summary>
/// App-only credential against Azure Resource Manager (ARM) for Resource Graph
/// reads. <see cref="ScopeFilter"/> is OPTIONAL — a comma-separated list of
/// subscription GUIDs or a management-group id used to scope the ARG query. It
/// is NEVER concatenated into KQL; the source validates each entry and passes
/// subscription GUIDs through the ARG request body's "subscriptions"/"
/// managementGroups" arrays.
/// </summary>
internal sealed record AzureResourceGraphCredentials(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string? ScopeFilter);

internal static class AzureResourceGraphCredentialReader
{
    public const string CredentialName = "azureresourcegraph";

    public static async Task<AzureResourceGraphCredentials?> ReadAsync(CredentialProtector protector, Guid tenantId)
    {
        // Honor per-project credential-name override (source first, sink fallback) —
        // matches EntraID exactly.
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await protector.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            var sinkName = CredentialNameContext.Resolve(CredentialName, CredentialSide.Sink);
            if (!string.Equals(sinkName, name, StringComparison.OrdinalIgnoreCase))
                raw = await protector.RetrieveAsync(tenantId, sinkName);
        }
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var t = doc.RootElement.TryGetProperty("TenantId", out var tEl) ? tEl.GetString() : null;
            var c = doc.RootElement.TryGetProperty("ClientId", out var cEl) ? cEl.GetString() : null;
            var s = doc.RootElement.TryGetProperty("ClientSecret", out var sEl) ? sEl.GetString() : null;
            var scope = doc.RootElement.TryGetProperty("ScopeFilter", out var scEl) ? scEl.GetString() : null;
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || string.IsNullOrEmpty(s)) return null;
            return new AzureResourceGraphCredentials(t, c, s, string.IsNullOrWhiteSpace(scope) ? null : scope);
        }
        catch
        {
            return null;
        }
    }
}
