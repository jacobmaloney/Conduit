using System.Text.Json;
using Conduit.Core.Models;

namespace Conduit.Web.Services;

/// <summary>
/// The body of POST api/agent/heartbeat (ZERO-TOUCH-01): besides version and capabilities, what this installation
/// calls itself and the connected systems it serves, so IdentityCenter can give a valid key its registration back
/// (UC038-D04) and turn each connected system into a connection before anything has synced (UC038-D02). Credentials
/// never travel: a connected system is announced by name, kind and domain hint only.
/// </summary>
public static class IcHeartbeatBody
{
    /// <summary>Tenant.SystemType values that are IdentityCenter itself (its sinks), never announced as connections.</summary>
    private static readonly string[] IdentityCenterKinds = { "IdentityCenter", "CertificationCenter" };

    public sealed record ConnectedSystem(string Name, string? SystemType, string? Domain);

    /// <summary>The active connected systems worth announcing: every one that is not an IdentityCenter sink.</summary>
    public static IReadOnlyList<ConnectedSystem> Announceable(IEnumerable<Tenant> tenants) =>
        tenants.Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.Name)
                           && !IdentityCenterKinds.Contains(t.SystemType, StringComparer.OrdinalIgnoreCase))
               .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
               .Select(t => new ConnectedSystem(t.Name.Trim(), t.SystemType, string.IsNullOrWhiteSpace(t.Domain) ? null : t.Domain.Trim()))
               .ToList();

    public static string Build(string version, IReadOnlyList<string> capabilities, string name, string machineName, IReadOnlyList<ConnectedSystem> connectedSystems) =>
        JsonSerializer.Serialize(new
        {
            version,
            capabilities,
            name,
            machineName,
            connectedSystems = connectedSystems.Select(s => new { name = s.Name, systemType = s.SystemType, domain = s.Domain }).ToArray(),
        });

    /// <summary>The reason IdentityCenter refused a heartbeat with, from its JSON body; the bare status when there is none.</summary>
    public static string DescribeRefusal(int status, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    return $"HTTP {status}: {error.GetString()}";
            }
            catch (JsonException) { /* not JSON: the status alone */ }
        }
        return $"HTTP {status}";
    }
}
