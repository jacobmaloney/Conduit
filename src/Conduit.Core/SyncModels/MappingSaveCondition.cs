using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Conduit.Core.SyncModels;

public sealed record MappingSaveCondition(string Revision)
{
    public static string Version(SyncProject project, WorkflowStep step, IEnumerable<AttributeMapping> mappings,
        SyncProjectScope? scope = null, IEnumerable<AttributeMapping>? projectMappings = null)
    {
        var saved = mappings.ToArray();
        IEnumerable<AttributeMapping> inherited = saved.Length == 0 ? projectMappings ?? [] : [];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Project = new { project.Id, project.SourceTenantId, project.SinkTenantId, project.SinkTable, project.LastModified, project.SourceCredentialName, project.SinkCredentialName, project.ObjectClass },
            Step = new { step.Id, step.WorkflowId, step.StepType, step.ObjectClass, step.Enabled, step.ModifiedAt, step.Configuration },
            Scope = new { scope?.BaseDN, scope?.IncludedBaseDNs, scope?.ExcludedBaseDNs, scope?.LdapFilter, scope?.QueryExpression,
                PageSize = scope?.PageSize ?? 1000, scope?.MaxObjects, IncludeDeleted = scope?.IncludeDeleted ?? false },
            Mappings = saved.OrderBy(m => m.Id).Select(m => new { m.Id, m.SourceAttribute, m.SinkAttribute, m.TransformExpr, m.IsRequired, m.SortOrder }),
            InheritedMappings = inherited.OrderBy(m => m.Id).Select(m => new { m.Id, m.SourceAttribute, m.SinkAttribute, m.TransformExpr, m.IsRequired, m.SortOrder })
        }))));
    }
}
