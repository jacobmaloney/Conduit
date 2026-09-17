using Conduit.Connectors.ActiveDirectory;
using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.SourceBrowsing;

namespace Conduit.Web.Services;

public interface ISourceMappingTargetService
{
    Task<SourceMappingSchema> ReadAsync(Tenant target, SyncProject project, WorkflowStep step, CancellationToken ct);
}
public sealed class SourceMappingTargetService(IIdentityCenterSourceCatalogClient ic) : ISourceMappingTargetService
{
    public async Task<SourceMappingSchema> ReadAsync(Tenant target, SyncProject project, WorkflowStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!target.IsActive) throw new SourceBrowseException("InactiveTarget", "Enable the target connection before saving mappings.");
        if (target.SystemType is "IdentityCenter" or "CertificationCenterSaaS")
            return await ic.MappingSchemaAsync(target.Id, project.SinkTable ?? "Objects", project.SinkCredentialName, ct);
        if (target.SystemType == "ActiveDirectory" && ActiveDirectorySink.MappingSchema(step.ObjectClass ?? project.ObjectClass) is { } schema) return schema;
        throw new SourceBrowseException("TargetSchemaUnavailable", "This destination does not expose supported mapping fields here yet. Use its project editor.");
    }
}
