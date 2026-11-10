using System.Text.Json;

namespace Conduit.Web.Services;

/// <summary>
/// An IdentityCenter sync project as IdentityCenter defines it (ZERO-TOUCH-01): read from GET api/jobs/{id}/project
/// by the installation holding the job, and mirrored into a local project by <see cref="IcProjectMirror"/>. The
/// shape is IdentityCenter's <c>SyncProjectDefinition</c>; the fingerprint is IdentityCenter's, so "unchanged" is
/// one string comparison.
/// </summary>
public sealed record IcProjectDefinition(
    Guid ProjectId,
    string Name,
    string? Description,
    string ProjectType,
    IcProjectConnection? SourceConnection,
    IReadOnlyList<IcProjectWorkflow> Workflows,
    string Fingerprint);

public sealed record IcProjectConnection(Guid Id, string Name, string? ProviderType, string Transport, Guid? AgentId);

public sealed record IcProjectWorkflow(string Name, string? Description, string ObjectClass, string WorkflowType, int ExecutionOrder, bool IsEnabled, IReadOnlyList<IcProjectStep> Steps);

public sealed record IcProjectStep(
    string Name,
    int ExecutionOrder,
    string ObjectClass,
    string? StepType,
    string? LdapFilter,
    string? SearchBase,
    IReadOnlyList<string> SearchBases,
    IReadOnlyList<string> ExcludedSearchBases,
    string SearchScope,
    int LdapPageSize,
    bool IsEnabled,
    IReadOnlyList<IcProjectMapping> Mappings);

public sealed record IcProjectMapping(string SourceAttribute, string TargetAttribute, string? TargetType, string? TransformationType, string? TransformationExpression, bool IsRequired, int ExecutionOrder);

/// <summary>What IdentityCenter answered: the definition, or the status and reason it refused with.</summary>
public sealed record IcProjectDefinitionAnswer(IcProjectDefinition? Definition, int Status, string? Reason)
{
    public bool Ok => Definition is not null;
}

public static class IcProjectDefinitionReader
{
    /// <summary>The definition as IdentityCenter writes it; null when the body is not one (no project id or fingerprint).</summary>
    public static IcProjectDefinition? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var projectId = Guid(root, "projectId");
            var fingerprint = Str(root, "fingerprint");
            if (projectId is null || projectId == System.Guid.Empty || string.IsNullOrWhiteSpace(fingerprint)) return null;

            IcProjectConnection? source = null;
            if (root.TryGetProperty("sourceConnection", out var conn) && conn.ValueKind == JsonValueKind.Object)
                source = new IcProjectConnection(Guid(conn, "id") ?? System.Guid.Empty, Str(conn, "name") ?? string.Empty, Str(conn, "providerType"), Str(conn, "transport") ?? string.Empty, Guid(conn, "agentId"));

            var workflows = new List<IcProjectWorkflow>();
            if (root.TryGetProperty("workflows", out var wfs) && wfs.ValueKind == JsonValueKind.Array)
            {
                foreach (var wf in wfs.EnumerateArray())
                {
                    if (wf.ValueKind != JsonValueKind.Object) continue;
                    var steps = new List<IcProjectStep>();
                    if (wf.TryGetProperty("steps", out var sts) && sts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var st in sts.EnumerateArray())
                        {
                            if (st.ValueKind != JsonValueKind.Object) continue;
                            var mappings = new List<IcProjectMapping>();
                            if (st.TryGetProperty("mappings", out var mps) && mps.ValueKind == JsonValueKind.Array)
                                foreach (var mp in mps.EnumerateArray())
                                    if (mp.ValueKind == JsonValueKind.Object)
                                        mappings.Add(new IcProjectMapping(Str(mp, "sourceAttribute") ?? string.Empty, Str(mp, "targetAttribute") ?? string.Empty,
                                            Str(mp, "targetType"), Str(mp, "transformationType"), Str(mp, "transformationExpression"), Bool(mp, "isRequired"), Int(mp, "executionOrder") ?? 0));
                            steps.Add(new IcProjectStep(Str(st, "name") ?? string.Empty, Int(st, "executionOrder") ?? 0, Str(st, "objectClass") ?? "User", Str(st, "stepType"),
                                Str(st, "ldapFilter"), Str(st, "searchBase"), Strings(st, "searchBases"), Strings(st, "excludedSearchBases"),
                                Str(st, "searchScope") ?? "Subtree", Int(st, "ldapPageSize") ?? 1000, Bool(st, "isEnabled", true), mappings));
                        }
                    }
                    workflows.Add(new IcProjectWorkflow(Str(wf, "name") ?? string.Empty, Str(wf, "description"), Str(wf, "objectClass") ?? "User",
                        Str(wf, "workflowType") ?? "FullSync", Int(wf, "executionOrder") ?? 0, Bool(wf, "isEnabled", true), steps));
                }
            }
            return new IcProjectDefinition(projectId.Value, Str(root, "name") ?? string.Empty, Str(root, "description"), Str(root, "projectType") ?? "ObjectSync", source, workflows, fingerprint!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static Guid? Guid(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.Guid.TryParse(v.GetString(), out var g) ? g : null;
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static bool Bool(JsonElement e, string name, bool fallback = false) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;
    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : new List<string>();
}
