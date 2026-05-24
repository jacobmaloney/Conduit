using System;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Security;

namespace Conduit.Connectors.EntraID;

internal sealed record EntraIDCredentials(string TenantId, string ClientId, string ClientSecret);

internal static class EntraIDCredentialReader
{
    public const string CredentialName = "entraid";

    public static async Task<EntraIDCredentials?> ReadAsync(CredentialProtector protector, Guid tenantId)
    {
        // Phase 3: honor per-project credential-name override (source first, sink fallback).
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
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || string.IsNullOrEmpty(s)) return null;
            return new EntraIDCredentials(t, c, s);
        }
        catch
        {
            return null;
        }
    }
}
