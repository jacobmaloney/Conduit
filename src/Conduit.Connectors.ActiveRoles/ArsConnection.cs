using System;
using System.DirectoryServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Resolved Active Roles connection settings used to construct an EDMS://
/// DirectoryEntry. Phase 1 carries only the bind identity (the EDMS:// provider
/// routes through the locally-installed AR Administration Service).
/// </summary>
public sealed record ArsConnectionSettings(
    string BindUser,
    string BindPassword,
    string? ArsServiceHost)
{
    /// <summary>
    /// Optional anchor DN used purely to validate the bind in
    /// <c>TestConnectionAsync</c> (the AR ADSI provider has no bindable bare-root
    /// moniker, so a probe needs a real object/container DN). When null, the test
    /// falls back to binding the provider's "rootDSE" object.
    /// </summary>
    public string? TestBindDn { get; init; }

    /// <summary>
    /// Optional raw Active Directory DC host used to resolve an <c>objectGUID</c>
    /// to its distinguishedName when the upstream source keys objects by GUID
    /// (the Conduit AD source emits objectGUID as SourceId). The AR ADSI provider
    /// CANNOT bind/search by objectGUID (it returns 0x80041452), but the raw DC
    /// answers a serverless <c>LDAP://&lt;host&gt;/&lt;GUID=Nformat&gt;</c> bind, so
    /// the sink resolves GUID → DN here and then binds <c>EDMS://&lt;dn&gt;</c> for
    /// the policy-aware write. When null/blank the sink falls back to
    /// <see cref="ArsServiceHost"/> (the AR service host is normally domain-joined
    /// and serves LDAP too); only when neither is set is the GUID path unavailable.
    /// The same <see cref="BindUser"/>/<see cref="BindPassword"/> bind the DC.
    /// </summary>
    public string? AdHost { get; init; }
}

/// <summary>
/// Seam that yields <see cref="ArsConnectionSettings"/> for a connector run.
///
/// Two implementations exist:
///   * <see cref="TenantCredentialArsConnectionResolver"/> — the in-Conduit path.
///     Reads Tenant + the AES-GCM "ars" credential blob, exactly like the AD
///     connector reads its "ldap" blob. This is what the DI'd adapter uses.
///   * The standalone CLI supplies a static resolver from a local appsettings
///     file so the live harness can run on the AR host WITHOUT a Conduit DB.
///
/// Keeping resolution behind this seam is what lets the same connector code
/// prove itself against live ARS from a DB-free console while running unchanged
/// inside Conduit.Web.
/// </summary>
public interface IArsConnectionResolver
{
    Task<ArsConnectionSettings?> ResolveAsync(CredentialSide side, CancellationToken cancellationToken);
}

/// <summary>
/// In-Conduit resolver: pulls the "ars" credential blob for the tenant via the
/// generic <see cref="CredentialProtector"/> store, honoring the per-run
/// <see cref="CredentialNameContext"/> override (Source vs Sink). Mirrors the AD
/// connector's <c>ReadCredsAsync</c> chain (sink name first, then source name).
/// </summary>
public sealed class TenantCredentialArsConnectionResolver : IArsConnectionResolver
{
    private const string CredentialType = "ars";

    private readonly Guid _tenantId;
    private readonly TenantRepository _tenantRepo;
    private readonly CredentialProtector _protector;

    public TenantCredentialArsConnectionResolver(
        Guid tenantId,
        TenantRepository tenantRepo,
        CredentialProtector protector)
    {
        _tenantId = tenantId;
        _tenantRepo = tenantRepo;
        _protector = protector;
    }

    public async Task<ArsConnectionSettings?> ResolveAsync(CredentialSide side, CancellationToken cancellationToken)
    {
        var name = CredentialNameContext.Resolve(CredentialType, side);
        var raw = await _protector.RetrieveAsync(_tenantId, name);
        if (string.IsNullOrEmpty(raw))
        {
            // Fall back to the OTHER side's resolved name (single shared cred).
            var otherSide = side == CredentialSide.Sink ? CredentialSide.Source : CredentialSide.Sink;
            var otherName = CredentialNameContext.Resolve(CredentialType, otherSide);
            if (!string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                raw = await _protector.RetrieveAsync(_tenantId, otherName);
        }
        if (string.IsNullOrEmpty(raw)) return null;

        ActiveRolesCredential? cred;
        try
        {
            cred = JsonSerializer.Deserialize<ActiveRolesCredential>(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        if (cred is null || string.IsNullOrWhiteSpace(cred.BindUser) || cred.BindPassword is null)
            return null;

        // arsServiceHost falls back to the Tenant.Domain hint when not in the blob.
        var serviceHost = cred.ArsServiceHost;
        if (string.IsNullOrWhiteSpace(serviceHost))
        {
            var tenant = await _tenantRepo.GetByIdAsync(_tenantId);
            serviceHost = tenant?.Domain;
        }

        // adHost (Phase-2 fast-read field) doubles as the raw-AD DC for objectGUID
        // → DN resolution in the sink. Fall back to the AR service host (normally
        // domain-joined, answers LDAP) when the operator left adHost blank.
        var adHost = string.IsNullOrWhiteSpace(cred.AdHost) ? serviceHost : cred.AdHost;

        return new ArsConnectionSettings(cred.BindUser!, cred.BindPassword!, serviceHost)
        {
            AdHost = adHost
        };
    }
}

/// <summary>
/// Static resolver used by the standalone CLI harness. Holds already-resolved
/// settings (read from a local, uncommitted appsettings.json / env / args) so
/// the connector can run against live ARS with no Conduit database.
/// </summary>
public sealed class StaticArsConnectionResolver : IArsConnectionResolver
{
    private readonly ArsConnectionSettings _settings;

    public StaticArsConnectionResolver(ArsConnectionSettings settings) => _settings = settings;

    public Task<ArsConnectionSettings?> ResolveAsync(CredentialSide side, CancellationToken cancellationToken)
        => Task.FromResult<ArsConnectionSettings?>(_settings);
}

/// <summary>
/// Helpers shared by the source + sink for binding an EDMS:// DirectoryEntry and
/// surfacing AR policy denials.
/// </summary>
internal static class ArsBind
{
    /// <summary>
    /// Build an EDMS:// path. When a DN is provided it is bound directly;
    /// otherwise the caller binds a container/base and searches.
    ///
    /// The AR ADSI provider's moniker is "EDMS://". Including the service host
    /// (EDMS://host/&lt;dn&gt;) targets a specific AR Administration Service; the
    /// bare EDMS://&lt;dn&gt; form uses the provider's default service. Both route
    /// through the AR service (NOT raw AD) — that is what makes policies, workflows
    /// and virtual attributes apply.
    /// </summary>
    public static string Path(string? serviceHost, string dnOrEmpty)
    {
        if (string.IsNullOrWhiteSpace(serviceHost))
            return string.IsNullOrEmpty(dnOrEmpty) ? "EDMS://" : $"EDMS://{dnOrEmpty}";
        var host = serviceHost.Trim();
        return string.IsNullOrEmpty(dnOrEmpty) ? $"EDMS://{host}/" : $"EDMS://{host}/{dnOrEmpty}";
    }

    public static DirectoryEntry Bind(ArsConnectionSettings s, string dnOrEmpty)
        => new DirectoryEntry(Path(s.ArsServiceHost, dnOrEmpty), s.BindUser, s.BindPassword);
}

/// <summary>
/// Resolves an Active Directory <c>objectGUID</c> to its distinguishedName by
/// binding the RAW AD DC (NOT the AR ADSI provider) with a serverless GUID
/// moniker: <c>LDAP://&lt;host&gt;/&lt;GUID=Nformat&gt;</c>. This is the bridge that
/// lets the ARS sink accept an objectGUID SourceId (emitted by the Conduit AD
/// source) even though the AR provider itself cannot bind/search by objectGUID
/// (it returns 0x80041452). Once the DN is known the caller binds
/// <c>EDMS://&lt;dn&gt;</c> for the policy-aware write.
/// </summary>
internal static class AdGuidResolver
{
    /// <summary>
    /// Bind <c>LDAP://&lt;host&gt;/&lt;GUID=N&gt;</c> against the raw DC and read the
    /// object's distinguishedName. Returns null when no host is available or the
    /// bind/read fails (caller then surfaces the resolution error). The GUID is
    /// formatted as 32 hex digits with no braces or dashes (the "N" format), which
    /// is what AD's serverless GUID moniker expects.
    /// </summary>
    public static string? ResolveDn(
        ArsConnectionSettings s, Guid objectGuid,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        // GUID moniker format proven live on the lab DC (2026-06-11): the bind
        // requires the DASHED Guid form ("D": 8-4-4-4-12). The dashless "N" form and
        // the byte-escaped form both fail with "no such object" / "invalid dn
        // syntax". A serverless LDAP://<GUID=...> resolves via the default domain;
        // when an adHost is supplied we target it explicitly (also proven to work),
        // which lets the connector run off the domain it's syncing.
        var host = string.IsNullOrWhiteSpace(s.AdHost) ? s.ArsServiceHost : s.AdHost;
        var guidMoniker = $"<GUID={objectGuid:D}>";
        var path = string.IsNullOrWhiteSpace(host)
            ? $"LDAP://{guidMoniker}"
            : $"LDAP://{host.Trim()}/{guidMoniker}";
        try
        {
            using var entry = new DirectoryEntry(path, s.BindUser, s.BindPassword);
            // RefreshCache forces the bind/read now so a failure throws here rather
            // than lazily later. distinguishedName is an operational/base attribute.
            entry.RefreshCache(new[] { "distinguishedName" });
            var dn = entry.Properties["distinguishedName"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(dn))
            {
                // Fall back to the bound entry's own DN (entry.Properties["distinguishedName"]
                // can be empty on some providers; the Path's CN/Name still resolves it).
                dn = entry.Properties["dn"].Value?.ToString();
            }
            if (string.IsNullOrWhiteSpace(dn))
            {
                logger.LogWarning("ARS sink: LDAP GUID bind for {Guid} on {Host} returned no distinguishedName.", objectGuid, host);
                return null;
            }
            return dn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ARS sink: raw-AD LDAP GUID bind failed for {Guid} on {Host}.", objectGuid, host);
            return null;
        }
    }
}

/// <summary>
/// Shared connection probe for the source + sink <c>TestConnectionAsync</c>.
/// The AR ADSI provider has no bindable bare-root moniker, so the probe binds a
/// real anchor: the configured <see cref="ArsConnectionSettings.TestBindDn"/>
/// when present, else the provider's "rootDSE" object (which exposes
/// defaultNamingContext). A successful RefreshCache proves the provider is
/// installed AND the AR service accepted the bind.
/// </summary>
internal static class ArsProbe
{
    public static ConnectorTestResult Test(ArsConnectionSettings s, Microsoft.Extensions.Logging.ILogger logger)
    {
        // Prefer an explicit anchor DN; otherwise probe rootDSE.
        if (!string.IsNullOrWhiteSpace(s.TestBindDn))
        {
            try
            {
                using var anchor = ArsBind.Bind(s, s.TestBindDn!);
                anchor.RefreshCache();
                var name = anchor.Name;
                return new ConnectorTestResult
                {
                    IsSuccessful = true,
                    Message = $"Bound through the Active Roles ADSI provider (EDMS://) to '{s.TestBindDn}'. Object: {name}."
                };
            }
            catch (Exception ex)
            {
                return new ConnectorTestResult { IsSuccessful = false, Message = ex.Message };
            }
        }

        try
        {
            using var rootDse = ArsBind.Bind(s, "rootDSE");
            rootDse.RefreshCache();
            var dnc = rootDse.Properties["defaultNamingContext"].Value?.ToString();
            return new ConnectorTestResult
            {
                IsSuccessful = true,
                Message = $"Bound through the Active Roles ADSI provider (EDMS://). defaultNamingContext: {dnc ?? "<unknown>"}."
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ARS rootDSE probe failed.");
            return new ConnectorTestResult
            {
                IsSuccessful = false,
                Message = $"{ex.Message} (Tip: supply a TestBindDn anchor — an OU or user DN — if rootDSE is not bindable through this provider build.)"
            };
        }
    }

    /// <summary>
    /// Resolve named virtual/real attributes for a single object by binding
    /// <c>EDMS://&lt;dn&gt;</c> and reading <c>Properties[name].Value</c> — the
    /// proven through-ARS read path (this is how the live PowerShell proof read
    /// VAs). Used by the source to surface VAs that a DirectorySearcher projection
    /// does not return. Missing/unreadable attributes are simply omitted.
    /// </summary>
    public static Dictionary<string, object?> ResolveAttributes(
        ArsConnectionSettings s, string dn, IReadOnlyList<string> names,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        try
        {
            using var entry = ArsBind.Bind(s, dn);
            // Full RefreshCache materializes the object including its resolved
            // (virtual) attributes — more robust than a property-scoped refresh,
            // which can throw when one named VA isn't applicable to the object.
            entry.RefreshCache();
            foreach (var name in names)
            {
                try
                {
                    var prop = entry.Properties[name];
                    if (prop.Value is not null) result[name] = prop.Value;
                }
                catch (Exception exAttr)
                {
                    logger.LogDebug(exAttr, "ARS attribute '{Attr}' not readable on {Dn}", name, dn);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ARS per-object attribute resolve failed for {Dn}", dn);
        }
        return result;
    }
}
