using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Graph.Models;

namespace Conduit.Connectors.EntraID;

/// <summary>
/// Projects an application's / service principal's passwordCredentials and
/// keyCredentials down to EXPIRY METADATA for the NHI / credential-hygiene read
/// side. The only fields read are keyId, displayName, type, usage, startDateTime,
/// endDateTime, hint (password) and customKeyIdentifier (key). <c>KeyCredential.Key</c>
/// (the certificate / public-key blob) and <c>PasswordCredential.SecretText</c> are
/// never read, serialized or logged — Graph does not return secretText on a read,
/// but nothing here depends on that.
/// </summary>
internal static class EntraCredentialMetadata
{
    public const int ExpiringSoonDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Entry(
        string? keyId,
        string? displayName,
        string kind,
        string? type,
        string? usage,
        string? startDateTime,
        string? endDateTime,
        string? hint,
        string? customKeyIdentifier)
    {
        [JsonIgnore] public DateTimeOffset? End { get; init; }
    }

    /// <summary>
    /// Stamps credentialCount / hasExpiredCredential / hasCredentialExpiringWithin30d on
    /// every object, and credentials (compact JSON array) + earliestCredentialExpiry (ISO)
    /// only when at least one credential exists.
    /// </summary>
    public static void Apply(
        Dictionary<string, object?> attrs,
        IList<PasswordCredential>? passwords,
        IList<KeyCredential>? keys,
        DateTimeOffset now)
    {
        var entries = new List<Entry>();
        if (passwords is not null)
            foreach (var p in passwords)
                entries.Add(new Entry(
                    p.KeyId?.ToString(), p.DisplayName, "password", null, null,
                    Iso(p.StartDateTime), Iso(p.EndDateTime), p.Hint, null)
                { End = p.EndDateTime });
        if (keys is not null)
            foreach (var k in keys)
                entries.Add(new Entry(
                    k.KeyId?.ToString(), k.DisplayName, "key", k.Type, k.Usage,
                    Iso(k.StartDateTime), Iso(k.EndDateTime), null,
                    k.CustomKeyIdentifier is { Length: > 0 } ck ? Convert.ToBase64String(ck) : null)
                { End = k.EndDateTime });

        entries.Sort((a, b) => (a.End ?? DateTimeOffset.MaxValue).CompareTo(b.End ?? DateTimeOffset.MaxValue));

        var soon = now.AddDays(ExpiringSoonDays);
        DateTimeOffset? earliest = null;
        var expired = false;
        var expiringSoon = false;
        foreach (var e in entries)
        {
            if (e.End is not { } end) continue;
            if (earliest is null || end < earliest.Value) earliest = end;
            if (end <= now) expired = true;
            else if (end <= soon) expiringSoon = true;
        }

        attrs["credentialCount"] = entries.Count;
        attrs["hasExpiredCredential"] = expired;
        attrs["hasCredentialExpiringWithin30d"] = expiringSoon;
        if (entries.Count == 0) return;
        attrs["credentials"] = JsonSerializer.Serialize(entries, JsonOptions);
        if (earliest.HasValue) attrs["earliestCredentialExpiry"] = earliest.Value.ToString("o");
    }

    private static string? Iso(DateTimeOffset? value) => value?.ToString("o");
}
