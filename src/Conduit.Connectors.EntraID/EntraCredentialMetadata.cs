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
/// endDateTime and customKeyIdentifier (key — a public-cert thumbprint).
/// <c>KeyCredential.Key</c> (the certificate / public-key blob),
/// <c>PasswordCredential.SecretText</c> AND <c>PasswordCredential.Hint</c> (the first
/// characters of the live secret) are never read, serialized or logged — Graph does
/// not return secretText on a read, but nothing here depends on that.
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
        string? customKeyIdentifier)
    {
        [JsonIgnore] public DateTimeOffset? Start { get; init; }
        [JsonIgnore] public DateTimeOffset? End { get; init; }
    }

    /// <summary>
    /// Stamps credentialCount / expiredCredentialCount / hasExpiredCredential /
    /// hasCredentialExpiringWithin30d on every object, and credentials (compact JSON
    /// array) + earliestCredentialExpiry + oldestCredentialCreatedAt (ISO; rotation
    /// staleness — the earliest startDateTime) only when at least one credential
    /// exists. Date rollups are absent — never MinValue — when no credential carries
    /// the underlying date.
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
                    Iso(p.StartDateTime), Iso(p.EndDateTime), null)
                { Start = p.StartDateTime, End = p.EndDateTime });
        if (keys is not null)
            foreach (var k in keys)
                entries.Add(new Entry(
                    k.KeyId?.ToString(), k.DisplayName, "key", k.Type, k.Usage,
                    Iso(k.StartDateTime), Iso(k.EndDateTime),
                    k.CustomKeyIdentifier is { Length: > 0 } ck ? Convert.ToBase64String(ck) : null)
                { Start = k.StartDateTime, End = k.EndDateTime });

        entries.Sort((a, b) => (a.End ?? DateTimeOffset.MaxValue).CompareTo(b.End ?? DateTimeOffset.MaxValue));

        var soon = now.AddDays(ExpiringSoonDays);
        DateTimeOffset? earliest = null;
        DateTimeOffset? oldestStart = null;
        var expiredCount = 0;
        var expiringSoon = false;
        foreach (var e in entries)
        {
            if (e.Start is { } start && (oldestStart is null || start < oldestStart.Value))
                oldestStart = start;
            if (e.End is not { } end) continue;
            if (earliest is null || end < earliest.Value) earliest = end;
            if (end <= now) expiredCount++;
            else if (end <= soon) expiringSoon = true;
        }

        attrs["credentialCount"] = entries.Count;
        attrs["expiredCredentialCount"] = expiredCount;
        attrs["hasExpiredCredential"] = expiredCount > 0;
        attrs["hasCredentialExpiringWithin30d"] = expiringSoon;
        if (entries.Count == 0) return;
        attrs["credentials"] = JsonSerializer.Serialize(entries, JsonOptions);
        if (earliest.HasValue) attrs["earliestCredentialExpiry"] = earliest.Value.ToString("o");
        if (oldestStart.HasValue) attrs["oldestCredentialCreatedAt"] = oldestStart.Value.ToString("o");
    }

    private static string? Iso(DateTimeOffset? value) => value?.ToString("o");
}
