using Conduit.SourceBrowsing;
using Conduit.Sync.Security;

namespace Conduit.Connectors.IdentityCenter;

public interface IIdentityCenterSourceCatalogClient
{
    Task<SourceCatalog> ReadAsync(Guid tenantId, CancellationToken ct);
    Task<SourceMappingSchema> MappingSchemaAsync(Guid tenantId, string table, string? credentialName, CancellationToken ct) =>
        throw new SourceBrowseException("TargetSchemaUnavailable", "Update the IdentityCenter connector to load mapping targets.");
}

/// <summary>Read-only metadata through the existing IC credential owner. No source reads on the API host.</summary>
public sealed class IdentityCenterSourceCatalogClient(CredentialProtector credentials, IHttpClientFactory clients) : IIdentityCenterSourceCatalogClient
{
    public const string HttpClientName = "IdentityCenterSourceCatalog";
    public async Task<SourceMappingSchema> MappingSchemaAsync(Guid tenantId, string table, string? credentialName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // Both names are the selected sink name: never borrow another credential for schema discovery.
            using var names = CredentialNameContext.Push(credentialName, credentialName);
            var saved = await IdentityCenterCredentialReader.ReadAsync(credentials, tenantId, CredentialSide.Sink)
                ?? throw new SourceBrowseException("NotConfigured", "Save the target IdentityCenter credentials before loading mapping targets.");
            timeout.Token.ThrowIfCancellationRequested();
            var target = string.Equals(table, "Identities", StringComparison.OrdinalIgnoreCase) ? "Identities" : "Objects";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{saved.BaseUrl}/api/source-catalog/mapping-targets?table={target}");
            request.Headers.Add("X-API-Key", saved.ApiKey);
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new SourceBrowseException("TargetSchemaUnavailable", "Target fields could not be loaded. Check the target API version and the selected credentials.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var schema = await SourceBrowseJson.ReadAsync<SourceMappingSchema>(stream, timeout.Token);
            if (schema.Targets == null || schema.Targets.Count is < 1 or > 2000 || schema.Targets.Any(t => t == null || string.IsNullOrWhiteSpace(t.Name)) ||
                schema.Targets.Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != schema.Targets.Count)
                throw new SourceBrowseException("InvalidTargetSchema", "The target returned an invalid mapping schema.");
            return schema;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceBrowseException) { throw; }
        catch { throw new SourceBrowseException("TargetSchemaUnavailable", "Could not load mapping targets from IdentityCenter. Check its API availability and saved credentials."); }
    }
    public async Task<SourceCatalog> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var names = CredentialNameContext.Push(null, null);
            var saved = await IdentityCenterCredentialReader.ReadAsync(credentials, tenantId, CredentialSide.Source)
                ?? throw new SourceBrowseException("NotConfigured", "Save the IdentityCenter connection's credentials before loading its catalog.");
            timeout.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{saved.BaseUrl}/api/source-catalog");
            request.Headers.Add("X-API-Key", saved.ApiKey);
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new SourceBrowseException("AccessDenied", "IdentityCenter rejected catalog access. Check the saved tenant sync key.");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new SourceBrowseException("EndpointMissing", "This IdentityCenter deployment does not expose the source catalog yet. Update its API.");
            if (!response.IsSuccessStatusCode) throw new SourceBrowseException("RemoteFailure", $"IdentityCenter catalog returned HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var catalog = await SourceBrowseJson.ReadAsync<SourceCatalog>(stream, timeout.Token);
            if (catalog.Items == null || string.IsNullOrWhiteSpace(catalog.ExecutionLocation))
                throw new SourceBrowseException("InvalidResponse", "IdentityCenter returned an invalid source catalog.");
            return catalog;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SourceBrowseException("TimedOut", "IdentityCenter did not return its catalog in time."); }
        catch (OperationCanceledException) { throw; }
        catch (SourceBrowseException) { throw; }
        catch { throw new SourceBrowseException("RemoteFailure", "Could not load the IdentityCenter catalog. Check the saved connection and API availability."); }
    }
}
