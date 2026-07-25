using Conduit.DataAccess.Repositories;
using Microsoft.Extensions.Configuration;

namespace Conduit.Web.Services;

/// <summary>
/// Resolves the permitted AD creation base DNs for a connection — the SOURCE that feeds
/// <see cref="BaseDnContainment"/>. It does NOT decide containment (that logic is unchanged); it only
/// supplies the permitted list.
///
/// Resolution: the Conduit DB is authoritative — if a connection has DB entries, use them. If it has
/// none, fall back to IConfiguration ("AdProvisioning:CreationBaseDns:&lt;conn&gt;") so nothing regresses.
/// If NEITHER has entries, the list is empty ⇒ deny-all.
///
/// ★ FAIL-CLOSED: any DB read error / exception returns an EMPTY list (deny-all). A failure to load the
/// allow-list must never fall OPEN to "no restriction".
/// </summary>
/// <summary>Where a connection's ENFORCED permitted list came from.</summary>
public enum BaseDnSource { None, Database, Config }

public interface ICreationBaseDnPolicy
{
    /// <summary>The ENFORCED permitted base DNs used by the create path.</summary>
    Task<IReadOnlyList<string>> GetPermittedBaseDnsAsync(string sourceConnectionName);

    /// <summary>
    /// The enforced list PLUS its provenance — for the settings UI, so displayed state matches enforced
    /// state (single-sourced with <see cref="GetPermittedBaseDnsAsync"/>).
    /// </summary>
    Task<(IReadOnlyList<string> Enforced, BaseDnSource Source)> GetEffectiveAsync(string sourceConnectionName);

    /// <summary>The config-sourced base DNs for a connection, read EXACTLY the way the enforcement
    /// fallback reads them (so the UI can surface them without a divergent second read).</summary>
    IReadOnlyList<string> GetConfigBaseDns(string sourceConnectionName);

    /// <summary>Connection names that have an "AdProvisioning:CreationBaseDns:&lt;name&gt;" config section.</summary>
    IReadOnlyList<string> GetConfiguredConnectionNames();
}

public sealed class CreationBaseDnPolicy : ICreationBaseDnPolicy
{
    private const string ConfigSection = "AdProvisioning:CreationBaseDns";

    private readonly ICreationBaseDnAllowListRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreationBaseDnPolicy> _logger;

    public CreationBaseDnPolicy(
        ICreationBaseDnAllowListRepository repository,
        IConfiguration configuration,
        ILogger<CreationBaseDnPolicy> logger)
    {
        _repository = repository;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetPermittedBaseDnsAsync(string sourceConnectionName)
        => (await GetEffectiveAsync(sourceConnectionName)).Enforced;

    public async Task<(IReadOnlyList<string> Enforced, BaseDnSource Source)> GetEffectiveAsync(string sourceConnectionName)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName))
            return (System.Array.Empty<string>(), BaseDnSource.None);

        // DB is authoritative — and FAIL-CLOSED on any read error (deny-all, never fall open).
        List<string> dbDns;
        try
        {
            dbDns = await _repository.GetBaseDnsAsync(sourceConnectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CreationBaseDnPolicy: failed to read the creation base-DN allow-list for '{Name}' — denying by default (fail-closed).",
                sourceConnectionName);
            return (System.Array.Empty<string>(), BaseDnSource.None);
        }

        if (dbDns is { Count: > 0 })
            return (dbDns, BaseDnSource.Database);

        // No DB entries for this connection — fall back to config so an existing config-only deployment
        // does not regress.
        var config = GetConfigBaseDns(sourceConnectionName);
        return config.Count > 0
            ? (config, BaseDnSource.Config)
            : (System.Array.Empty<string>(), BaseDnSource.None);
    }

    public IReadOnlyList<string> GetConfigBaseDns(string sourceConnectionName)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName))
            return System.Array.Empty<string>();
        try
        {
            var values = _configuration.GetSection($"{ConfigSection}:{sourceConnectionName}").Get<string[]>();
            if (values is null || values.Length == 0)
                return System.Array.Empty<string>();
            return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CreationBaseDnPolicy: failed to read the config creation base-DN fallback for '{Name}' — denying by default.",
                sourceConnectionName);
            return System.Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetConfiguredConnectionNames()
    {
        try
        {
            return _configuration.GetSection(ConfigSection).GetChildren()
                .Select(c => c.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreationBaseDnPolicy: failed to enumerate configured connection names.");
            return System.Array.Empty<string>();
        }
    }
}
