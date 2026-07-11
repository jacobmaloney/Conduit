using System.Security.Cryptography;

namespace Conduit.Web.Services;

/// <summary>
/// Unattended first-run setup from a <c>Provision</c> configuration section
/// (stamped into appsettings.json by an installer or operator). When setup is
/// pending and <c>Provision:ConnectionString</c> is present, this applies the same
/// programmatic path as the /setup wizard (<see cref="SetupService.ApplySetupAsync"/>:
/// connection string, DatabaseInitializer, appsettings write, admin creation,
/// setup.complete marker) so the same boot continues straight into normal startup
/// and enrollment. Once setup is complete the caller never invokes this again, so a
/// second boot with the same provision config is a clean no-op; ApplySetupAsync's
/// own completion guard backstops that.
///
/// Supported keys: ConnectionString (required), AdminUsername (default "admin"),
/// AdminPassword (strong one generated when blank — persisted to
/// admin-initial-password.txt in the Conduit data dir BEFORE setup runs, and never
/// logged), JwtSecretKey (generated when blank), ServerPort (default 5500).
/// </summary>
public sealed class ProvisioningService
{
    private readonly SetupService _setupService;
    private readonly IConfiguration _config;
    private readonly ILogger<ProvisioningService> _logger;

    public ProvisioningService(SetupService setupService, IConfiguration config, ILogger<ProvisioningService> logger)
    {
        _setupService = setupService;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Applies provisioned setup if a provision config is present. Returns true only
    /// when setup completed successfully; false means "no provision config" or
    /// "provisioning failed — fall back to the /setup wizard". Only called while
    /// setup is still required.
    /// </summary>
    public async Task<bool> TryApplyAtStartupAsync()
    {
        var setup = TryBuildConfiguration(_config, out var passwordGenerated);
        if (setup is null)
            return false;

        var validation = _setupService.ValidateSetup(setup);
        if (!validation.IsValid)
        {
            foreach (var (field, messages) in validation.Errors)
                _logger.LogError("Provision config invalid — {Field}: {Errors}", field, string.Join("; ", messages));
            _logger.LogError("Provisioned setup skipped due to invalid config; falling back to the /setup wizard.");
            return false;
        }

        // Persist the generated password BEFORE mutating any state: if it cannot be
        // recorded the operator could never sign in (setup completes exactly once),
        // so abort to the wizard instead. The password itself is never logged.
        string? passwordFile = null;
        if (passwordGenerated)
        {
            passwordFile = WriteGeneratedAdminPasswordFile(setup.AdminUsername, setup.AdminPassword);
            if (passwordFile is null)
                return false;
        }

        _logger.LogInformation("Provision configuration detected — applying unattended setup (admin '{Admin}').", setup.AdminUsername);

        bool applied;
        try
        {
            applied = await _setupService.ApplySetupAsync(setup);
        }
        catch (SetupAlreadyCompletedException)
        {
            _logger.LogInformation("Provision config present but setup is already complete — nothing to do.");
            DeleteQuietly(passwordFile);
            return false;
        }

        if (!applied)
        {
            _logger.LogError("Provisioned setup failed (see errors above); falling back to the /setup wizard.");
            DeleteQuietly(passwordFile);
            return false;
        }

        if (passwordFile is not null)
            _logger.LogWarning(
                "Provision config had no AdminPassword — a strong password was generated and written to {Path}. Sign in, change it, then delete the file.",
                passwordFile);

        _logger.LogInformation("Provisioned setup complete: database initialized, portal admin '{Admin}' created.", setup.AdminUsername);
        return true;
    }

    /// <summary>
    /// Maps the Provision section to a <see cref="SetupConfiguration"/>, generating
    /// the admin password and JWT secret when blank. Null when no provision config
    /// is present (no ConnectionString). Pure except for the RNG — unit-testable.
    /// </summary>
    public static SetupConfiguration? TryBuildConfiguration(IConfiguration config, out bool passwordGenerated)
    {
        passwordGenerated = false;
        var section = config.GetSection("Provision");
        var connectionString = section["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var adminPassword = section["AdminPassword"];
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            // 24 chars over a 72-symbol alphabet ≈ 148 bits — well past any policy.
            adminPassword = RandomNumberGenerator.GetString(
                "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*-_=+", 24);
            passwordGenerated = true;
        }

        var jwtSecret = section["JwtSecretKey"];
        if (string.IsNullOrWhiteSpace(jwtSecret))
            jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        var setup = new SetupConfiguration
        {
            ConnectionString = connectionString.Trim(),
            AdminUsername = string.IsNullOrWhiteSpace(section["AdminUsername"]) ? "admin" : section["AdminUsername"]!.Trim(),
            AdminPassword = adminPassword,
            JwtSecretKey = jwtSecret,
            AutoCreateDatabase = true
        };

        if (int.TryParse(section["ServerPort"], out var port) && port > 0)
            setup.ServerPort = port;

        return setup;
    }

    /// <summary>Returns the file path on success, null on failure (already logged).</summary>
    private string? WriteGeneratedAdminPasswordFile(string username, string password)
    {
        var path = ConduitDataPaths.FilePath("admin-initial-password.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"Conduit portal admin credentials (generated by provisioned setup {DateTime.UtcNow:O})" + Environment.NewLine +
                $"Username: {username}" + Environment.NewLine +
                $"Password: {password}" + Environment.NewLine +
                "Sign in, change the password, then delete this file." + Environment.NewLine);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not persist the generated admin password to {Path} — aborting provisioned setup so the install is not left with an unknowable admin password. Fix the data directory or supply Provision:AdminPassword.",
                path);
            return null;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch { /* best effort — the file only matters after a successful setup */ }
    }
}
