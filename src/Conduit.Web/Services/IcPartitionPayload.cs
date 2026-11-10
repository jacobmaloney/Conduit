using System.Text.Json;
using Conduit.Core.SyncModels;

namespace Conduit.Web.Services;

/// <summary>
/// IdentityCenter SPLIT-ENUM-01: how a claimed job says what kind of read it is. The plan item of a project that reads
/// its source one container at a time carries <c>"Split":"Container"</c> and no partition; each container item carries a
/// <c>Partition</c> object (Index, Count, Label, IncludedBaseDNs, OneLevel, CarriesTombstones, ParentJobId), written by
/// IdentityCenter's <c>SyncEnumerationSplit</c>. Keys are read case-insensitively, like every other payload key here. A
/// malformed partition is not one: the job is refused rather than run as a whole read under a container's name.
/// </summary>
public static class IcPartitionPayload
{
    public const string ByContainer = "Container";
    public const string SplitKey = "Split";
    public const string PartitionKey = "Partition";

    public static bool IsPlan(string? payloadJson)
    {
        if (!TryParse(payloadJson, out var doc)) return false;
        using (doc)
        {
            var root = doc!.RootElement;
            return Find(root, SplitKey) is { ValueKind: JsonValueKind.String } split
                && string.Equals(split.GetString(), ByContainer, StringComparison.OrdinalIgnoreCase)
                && Find(root, PartitionKey) is null;
        }
    }

    public static bool HasPartition(string? payloadJson)
    {
        if (!TryParse(payloadJson, out var doc)) return false;
        using (doc) return Find(doc!.RootElement, PartitionKey) is not null;
    }

    public static SyncRunPartition? ReadPartition(string? payloadJson)
    {
        if (!TryParse(payloadJson, out var doc)) return null;
        using (doc)
        {
            if (Find(doc!.RootElement, PartitionKey) is not { ValueKind: JsonValueKind.Object } partition) return null;
            var index = Find(partition, "Index") is { ValueKind: JsonValueKind.Number } i && i.TryGetInt32(out var iv) ? iv : 0;
            var count = Find(partition, "Count") is { ValueKind: JsonValueKind.Number } c && c.TryGetInt32(out var cv) ? cv : 0;
            var dns = Find(partition, "IncludedBaseDNs") is { ValueKind: JsonValueKind.Array } bases
                ? bases.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList()
                : new List<string>();
            if (index < 1 || count < index || dns.Count == 0) return null;
            var label = Find(partition, "Label") is { ValueKind: JsonValueKind.String } l ? l.GetString() : null;
            return new SyncRunPartition(index, count, string.IsNullOrWhiteSpace(label) ? dns[0] : label!.Trim(), dns,
                Find(partition, "OneLevel") is { ValueKind: JsonValueKind.True },
                Find(partition, "CarriesTombstones") is { ValueKind: JsonValueKind.True });
        }
    }

    private static bool TryParse(string? payloadJson, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            var parsed = JsonDocument.Parse(payloadJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) { parsed.Dispose(); return false; }
            doc = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static JsonElement? Find(JsonElement element, string key)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return null;
    }
}
