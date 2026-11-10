using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Core.SyncModels;

/// <summary>
/// One container of a split read (IdentityCenter SPLIT-ENUM-01, UC037-D06). IdentityCenter queues one job per
/// container of a project that reads its source one container at a time; the job's payload carries this and the run
/// reads only it. <see cref="OneLevel"/> is the base itself one level deep (its own objects, not its child
/// containers'); <see cref="CarriesTombstones"/> marks the one container of a plan that runs the deleted-objects pass
/// (UC037-D09). A partition run never advances a step's cursor: a partial read must not move the watermark the next
/// full read resumes from.
/// </summary>
public sealed record SyncRunPartition(
    int Index,
    int Count,
    string Label,
    IReadOnlyList<string> IncludedBaseDNs,
    bool OneLevel,
    bool CarriesTombstones)
{
    public string Describe() => $"Partition {Index}/{Count}: {Label}" + (OneLevel ? " (this level only)" : string.Empty);

    /// <summary>
    /// The step's scope reading only this container: its included bases replaced by the container, its excluded bases,
    /// filter, page size and caps kept, the read marked one level deep when the container is the base itself, and the
    /// deleted-objects pass left to the one container that carries it. The step's own scope object is not changed.
    /// </summary>
    public SyncProjectScope ApplyTo(SyncProjectScope scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (IncludedBaseDNs is null || IncludedBaseDNs.Count == 0 || IncludedBaseDNs.All(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{Describe()} names no container to read.");
        var copy = new SyncProjectScope
        {
            Id = scope.Id,
            SyncProjectId = scope.SyncProjectId,
            WorkflowStepId = scope.WorkflowStepId,
            ExcludedBaseDNs = scope.ExcludedBaseDNs,
            LdapFilter = scope.LdapFilter,
            QueryExpression = scope.QueryExpression,
            PageSize = scope.PageSize,
            MaxObjects = scope.MaxObjects,
            IncludeDeleted = scope.IncludeDeleted,
            RequestedAttributes = scope.RequestedAttributes,
            CreatedAt = scope.CreatedAt,
            LastModified = scope.LastModified,
            ContainerOnly = OneLevel,
            SuppressTombstones = !CarriesTombstones,
        };
        copy.SetIncludedBaseList(IncludedBaseDNs);
        return copy;
    }
}
