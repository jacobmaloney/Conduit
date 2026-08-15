using Conduit.DataAccess;
using Conduit.Web.Middleware;

namespace Conduit.Web.Services;

/// <summary>
/// Startup safety net for a database an admin explicitly designated but whose schema is
/// absent — the out-of-band cases the /database-settings prepare action cannot cover:
/// someone hand-edited secrets.json, set an environment variable, deployed with a
/// connection string, or a prepare run failed partway.
///
/// Without this, an empty database is a dead end: 0 tables →
/// <see cref="DatabaseStatus.NotConfigured"/> → <c>IsSetupRequiredAsync</c> true →
/// Program.cs gates <see cref="DatabaseInitializer"/> behind <c>if (!setupRequired)</c>,
/// so the only component that can build a schema never runs. "Restart to migrate" is
/// impossible, and <c>AutoMigrate=true</c> changes nothing.
///
/// The distinction this encodes: "a connection an admin explicitly designated + reachable
/// + schema absent" is materially different from "true first run, nothing configured."
/// Today both collapse to NotConfigured and both get routed to the wizard.
///
/// SECURITY: the authorization is the PIN, not a flag. A schema is built only when the
/// marker in secrets.json names the exact server AND database AND issuing machine that the
/// currently-resolved connection matches. No marker, or a marker naming anything else,
/// changes nothing at all and the existing first-run wizard runs as before. An unreachable
/// database is never "provision it", and neither is a database that already HAS a schema —
/// both are asserted explicitly below rather than inferred from a status value.
/// </summary>
public sealed class ProvisionedSchemaService
{
    private readonly DatabaseConfig _databaseConfig;
    private readonly SetupService _setupService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProvisionedSchemaService> _logger;

    public ProvisionedSchemaService(DatabaseConfig databaseConfig, SetupService setupService,
        ILoggerFactory loggerFactory, ILogger<ProvisionedSchemaService> logger)
    {
        _databaseConfig = databaseConfig;
        _setupService = setupService;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Test seam: read the marker from somewhere other than the real data dir.</summary>
    public string? SecretsPathOverride { get; set; }

    /// <summary>
    /// The authorization decision, isolated from all I/O so it can be tested directly.
    /// <see cref="TryInitializeAtStartupAsync"/> calls exactly this — a schema is built for
    /// <see cref="ProvisionDecision.Migrate"/> and for nothing else.
    /// </summary>
    public static ProvisionDecision Decide(string? configuredConnectionString,
        ProvisionedConnectionTarget? marker, out ProvisionedConnectionTarget configured)
    {
        if (!ProvisionedConnectionMarker.TryParseConnectionString(configuredConnectionString, out configured))
        {
            return ProvisionDecision.NoConnection;
        }

        if (marker is null)
        {
            return ProvisionDecision.MarkerAbsent;
        }

        if (ProvisionedConnectionMarker.Matches(marker, configured))
        {
            return ProvisionDecision.Migrate;
        }

        // Distinguished from a plain mismatch so the startup log can say WHY: an install
        // copied to another host, or a restored secrets.json, matches on the host-relative
        // server name ("localhost") while naming a physically different server.
        return ProvisionedConnectionMarker.MatchesExceptMachine(marker, configured)
            ? ProvisionDecision.MachineMismatch
            : ProvisionDecision.MarkerMismatch;
    }

    /// <summary>
    /// Builds the schema when — and only when — the provisioning marker matches the
    /// configured database. Returns true when the database ends up Ready, so the caller
    /// can continue this boot into normal startup. Returns false for every other outcome
    /// (no marker, mismatch, migration failure, or migrated-but-no-admin-yet), which
    /// leaves the operator on the existing setup routing.
    /// </summary>
    public async Task<bool> TryInitializeAtStartupAsync()
    {
        // An unreachable host must NEVER be read as "provision it".
        //
        // This guard used to carry the whole meaning on its own, back when NotConfigured
        // implied schema-absent. It no longer does: "schema present, zero active admins" is
        // now DatabaseStatus.NeedsAdminRecovery, and if that were ever folded back into
        // NotConfigured, a marker-matched, fully populated production database with zero
        // active admins would get InitializeAsync() run against it unattended at boot with
        // AutoCreateDatabase/AutoMigrate = true. So the status check is kept as a cheap
        // early-out, and schema-absence is ASSERTED explicitly below before anything is
        // built. Do not delete that assertion on the grounds that the status implies it.
        var status = await _setupService.GetDatabaseStatusAsync();
        if (status != DatabaseStatus.NotConfigured)
        {
            return false;
        }

        var connectionString = _databaseConfig.ConnectionString;

        ProvisionedConnectionTarget? marker;
        try
        {
            marker = ProvisionedConnectionMarker.Read(SecretsPathOverride);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read the provisioning marker from the secret store; leaving setup to the /setup wizard.");
            return false;
        }

        var decision = Decide(connectionString, marker, out var configured);
        switch (decision)
        {
            case ProvisionDecision.NoConnection:
                _logger.LogInformation(
                    "No usable database is configured — leaving first-run setup to the /setup wizard.");
                return false;

            case ProvisionDecision.MarkerAbsent:
                _logger.LogInformation(
                    "Database '{Database}' on '{Server}' has no schema and no provisioning marker is present — routing to the /setup wizard.",
                    configured.InitialCatalog, configured.DataSource);
                return false;

            case ProvisionDecision.MarkerMismatch:
                _logger.LogWarning(
                    "Provisioning marker names '{MarkerDatabase}' on '{MarkerServer}' but the configured connection is '{Database}' on '{Server}'. " +
                    "The marker authorizes exactly one database, so no schema will be created — routing to the /setup wizard.",
                    marker!.InitialCatalog, marker.DataSource, configured.InitialCatalog, configured.DataSource);
                return false;

            case ProvisionDecision.MachineMismatch:
                _logger.LogWarning(
                    "Provisioning marker names the same server/database but was issued on machine '{MarkerMachine}', not '{Machine}'. " +
                    "Server names like 'localhost' are host-relative, so this marker cannot prove it was issued for the database " +
                    "this host resolves — no schema will be created. Re-run 'Create and prepare database' here to re-issue it.",
                    marker!.MachineName, configured.MachineName);
                return false;
        }

        // EXPLICIT schema-absence assertion. NotConfigured got us this far, but the whole
        // point of building a schema is that there is none — so confirm it directly against
        // the target rather than trusting a status enum to keep meaning what it means today.
        // A populated database reaching this point is a bug, and it must cost nothing.
        var probe = await _setupService.TestConnectionAsync(connectionString!);
        if (!probe.ServerReachable)
        {
            _logger.LogInformation(
                "Provisioning marker matches, but the server is not reachable right now — leaving routing to the offline page. ({Reason})",
                probe.ErrorMessage);
            return false;
        }

        if (probe.SchemaExists)
        {
            _logger.LogWarning(
                "Provisioning marker matches the configured database '{Database}' on '{Server}', but that database ALREADY HAS A SCHEMA. " +
                "The startup safety net only ever builds a schema where none exists; refusing to run migrations against it unattended.",
                configured.InitialCatalog, configured.DataSource);
            return false;
        }

        _logger.LogInformation(
            "Provisioning marker matches the configured database '{Database}' on '{Server}' and its schema is absent — building it now.",
            configured.InitialCatalog, configured.DataSource);

        try
        {
            var detached = new DatabaseConfig
            {
                ConnectionString = connectionString!,
                AutoCreateDatabase = true,
                AutoMigrate = true
            };

            await new DatabaseInitializer(detached, _loggerFactory.CreateLogger<DatabaseInitializer>())
                .InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to build the schema for provisioned database '{Database}' on '{Server}'. The app will start and route to /setup so the connection can be corrected.",
                configured.InitialCatalog, configured.DataSource);
            return false;
        }

        SetupService.ClearStatusCache();
        SetupMiddleware.ClearCache();

        var newStatus = await _setupService.GetDatabaseStatusAsync();
        if (newStatus == DatabaseStatus.Ready)
        {
            _logger.LogInformation(
                "Provisioned database '{Database}' migrated and ready.", configured.InitialCatalog);
            return true;
        }

        _logger.LogInformation(
            "Provisioned database '{Database}' migrated, but it has no portal admin yet. Creating the first admin is NOT an anonymous " +
            "action: a one-time recovery token has been written to the ACL-restricted data directory and must be presented at " +
            "/admin-recovery. See AdminRecoveryToken.",
            configured.InitialCatalog);
        return false;
    }
}

/// <summary>
/// What the startup safety net is authorized to do. Only <see cref="Migrate"/> builds a
/// schema; every other value falls through to the existing first-run wizard unchanged.
/// </summary>
public enum ProvisionDecision
{
    /// <summary>Nothing usable is configured — a true first run.</summary>
    NoConnection,
    /// <summary>No admin ever designated this database through the portal.</summary>
    MarkerAbsent,
    /// <summary>An admin designated a DIFFERENT server/database. Authorizes nothing here.</summary>
    MarkerMismatch,
    /// <summary>
    /// Same server/database text, but the marker was issued on a different machine — a
    /// copied install or a restored secrets.json. Host-relative names like "localhost"
    /// resolve elsewhere here, so this authorizes nothing.
    /// </summary>
    MachineMismatch,
    /// <summary>The configured database is exactly the one an admin prepared.</summary>
    Migrate
}
