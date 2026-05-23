using Dapper;
using Microsoft.Data.SqlClient;
using Conduit.DataAccess;

namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

// Idempotent migrator for the gw_* schema. Shares Conduit's DB.
public sealed class GwDatabaseInitializer
{
    private readonly DatabaseConfig _config;
    private readonly ILogger<GwDatabaseInitializer> _logger;
    private readonly IWebHostEnvironment _env;

    public GwDatabaseInitializer(DatabaseConfig config, ILogger<GwDatabaseInitializer> logger, IWebHostEnvironment env)
    {
        _config = config;
        _logger = logger;
        _env = env;
    }

    public async Task InitializeAsync()
    {
        var scriptPath = LocateSchemaScript();
        if (scriptPath is null)
        {
            _logger.LogWarning("GoogleWorkspaceSchema.sql not found — skipping gw_* schema apply.");
            return;
        }

        var script = await File.ReadAllTextAsync(scriptPath);
        var batches = script.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToArray();

        using var connection = new SqlConnection(_config.ConnectionString);
        await connection.OpenAsync();

        foreach (var batch in batches)
        {
            await connection.ExecuteAsync(batch, commandTimeout: _config.CommandTimeout);
        }

        _logger.LogInformation("Google Workspace emulator schema applied ({Batches} batches).", batches.Length);
    }

    private string? LocateSchemaScript()
    {
        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, "..", "..", "Database", "GoogleWorkspaceSchema.sql"),
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "Database", "GoogleWorkspaceSchema.sql"),
            Path.Combine(AppContext.BaseDirectory, "Database", "GoogleWorkspaceSchema.sql"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
