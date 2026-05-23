using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;

namespace Conduit.Web.Services;

/// <summary>
/// Singleton runtime view of the <c>Portal.OpenAccess</c> setting. When on, the
/// Blazor portal does not require login — the OpenAccessSignInMiddleware
/// synthesizes an admin principal for every portal request. The SCIM / REST /
/// SQL / ARS API surfaces are unaffected; they still demand <c>scim_</c> tokens.
///
/// Intended use is conference demos and local testing — explicitly documented as
/// such in the toggle UI and via a sidebar badge that's visible whenever the
/// mode is active.
///
/// Cached in-process so [Authorize] checks don't take a DB hit per request.
/// Refresh from DB happens at process start and after any in-process toggle.
/// </summary>
public class OpenAccessState
{
    private const string ConfigKey = "Portal.OpenAccess";
    private readonly DatabaseConfig _db;
    private readonly ILogger<OpenAccessState> _logger;
    private bool _enabled;

    public bool IsEnabled => _enabled;

    public OpenAccessState(DatabaseConfig db, ILogger<OpenAccessState> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hot-load the current value from the database. Called by the startup hosted
    /// service so the first request after a restart sees the right state.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            var value = await conn.ExecuteScalarAsync<string?>(
                "SELECT [Value] FROM SystemConfiguration WHERE [Key] = @Key",
                new { Key = ConfigKey });
            _enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("OpenAccessState initialized: {Enabled}", _enabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAccessState could not read {Key}; defaulting to disabled.", ConfigKey);
            _enabled = false;
        }
    }

    /// <summary>
    /// Persist the new value to the SystemConfiguration table and flip the
    /// in-process cache. Called from the Configuration page toggle and from
    /// the Setup wizard when the operator opts into open-access during install.
    /// MERGE pattern matches SystemConfigurationService.SetAsync exactly so the
    /// row shape stays consistent regardless of who writes it.
    /// </summary>
    public async Task SetAsync(bool enabled)
    {
        var value = enabled ? "true" : "false";
        const string description = "Portal-only open access — no login required when true.";
        using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(@"
            MERGE SystemConfiguration AS target
            USING (SELECT @Key AS [Key]) AS src
               ON target.[Key] = src.[Key]
            WHEN MATCHED THEN
                UPDATE SET [Value] = @Value, [Type] = 'Boolean', [Description] = @Description, [LastModified] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Key], [Value], [Type], [Description])
                VALUES (@Key, @Value, 'Boolean', @Description);",
            new { Key = ConfigKey, Value = value, Description = description });
        _enabled = enabled;
        _logger.LogWarning("OpenAccessState changed to {Enabled} — portal authentication is now {AuthStatus}.",
            enabled, enabled ? "OFF (open access)" : "ON");
    }
}
