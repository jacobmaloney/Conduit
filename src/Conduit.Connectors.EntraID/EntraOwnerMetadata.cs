using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Graph.Models;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Projects a directory object's owners (users or service principals) to
/// <c>owners</c> (JSON array of {id, displayName, upn}), <c>ownerCount</c>,
/// <c>ownerIds</c> (semicolon list of the owners' Entra object ids = their
/// SourceUniqueId on IC, so the read side can JOIN to Objects without parsing JSON)
/// and <c>ownerNames</c> (semicolon list of display names for display / policy
/// contains-checks without parsing JSON).
/// IC has no multi-owner edge table — Objects.OwnerObjectId is bound from the
/// single-valued AD 'managedBy' DN and nothing else — so owners land as attributes.
/// </summary>
internal static class EntraOwnerMetadata
{
    public static readonly string[] OwnerSelect = { "id", "displayName", "userPrincipalName" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Owner(string id, string? displayName, string? upn);

    /// <summary>Null owners = the fetch did not happen (403 / error): stamp nothing.</summary>
    public static void Apply(Dictionary<string, object?> attrs, IReadOnlyList<DirectoryObject>? owners)
    {
        if (owners is null) return;
        var projected = new List<Owner>(owners.Count);
        var ids = new List<string>(owners.Count);
        var names = new List<string>(owners.Count);
        foreach (var o in owners)
        {
            if (string.IsNullOrEmpty(o.Id)) continue;
            ids.Add(o.Id);
            var owner = o switch
            {
                User u => new Owner(o.Id, u.DisplayName, u.UserPrincipalName),
                ServicePrincipal sp => new Owner(o.Id, sp.DisplayName, null),
                _ => new Owner(o.Id, null, null)
            };
            projected.Add(owner);
            if (!string.IsNullOrEmpty(owner.displayName)) names.Add(owner.displayName);
        }
        attrs["ownerCount"] = projected.Count;
        if (projected.Count == 0) return;
        attrs["owners"] = JsonSerializer.Serialize(projected, JsonOptions);
        attrs["ownerIds"] = ids;
        if (names.Count > 0) attrs["ownerNames"] = names;
    }
}
