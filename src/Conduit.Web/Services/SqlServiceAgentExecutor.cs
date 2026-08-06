using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Conduit.Web.Services;

/// <summary>
/// Executes an IdentityCenter "StopSqlService" agent command: stop (or, in the
/// default dry-run, only REPORT) the SQL Server Windows service on a discovered
/// on-prem host. IC (Azure) has no line of sight to on-prem hosts; this Conduit
/// job server does, so the OS-level action runs here — the same trust-boundary
/// posture as SqlAgentWriteExecutor.
///
/// This is the decommission-execution verb behind Server Center's "propose
/// decommission" flow. It is deliberately narrow:
///
/// SECURITY CONSTRAINTS (all enforced before any remote call):
///   1  Strict typed parse; >8 KB → fail; schemaVersion must be 1.
///   2  dryRun defaults TRUE. A live stop requires dryRun=false, which IC only
///      sets after its own capability + step-up gates.
///   3  SERVICE-NAME ALLOW-LIST — the single most important control. Only a SQL
///      Server engine service may be targeted: MSSQLSERVER (default instance) or
///      MSSQL$<instance>. Anything else is refused with no remote call. This
///      executor can NEVER stop an arbitrary Windows service.
///   4  Host is validated to a hostname/FQDN shape and single-quote-escaped; it is
///      only ever an -ComputerName value, never concatenated into a command.
///   5  Runs under the agent's own service-account identity (integrated auth), the
///      same model as the WinRM scanner — no credential travels in the payload.
///   6  Bounded 3-minute timeout; the raw payload is never logged.
/// </summary>
public sealed class SqlServiceAgentExecutor
{
    private const int MaxPayloadBytes = 8 * 1024;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ONLY the SQL Server engine service. MSSQLSERVER (default) or MSSQL$INSTANCE.
    private static readonly Regex AllowedServiceName =
        new(@"^MSSQL(SERVER|\$[A-Za-z0-9_]{1,64})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A defensive hostname / FQDN shape (labels of letters/digits/hyphen, dot-joined).
    private static readonly Regex HostShape =
        new(@"^[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62}))*$", RegexOptions.Compiled);

    private readonly ILogger<SqlServiceAgentExecutor> _logger;

    public SqlServiceAgentExecutor(ILogger<SqlServiceAgentExecutor> logger) => _logger = logger;

    private sealed class StopSqlServicePayload
    {
        public int SchemaVersion { get; set; }
        public string? ServerName { get; set; }
        public string? InstanceName { get; set; }
        public bool DryRun { get; set; } = true;
    }

    public async Task<(bool Success, string Message)> ExecuteAsync(Guid commandId, string? payloadJson, CancellationToken ct)
    {
        // ── (1) strict, size-bounded parse ──────────────────────────────────
        if (string.IsNullOrWhiteSpace(payloadJson))
            return (false, "StopSqlService: empty payload.");
        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            return (false, $"StopSqlService: payload exceeds {MaxPayloadBytes / 1024} KB cap.");

        StopSqlServicePayload? p;
        try { p = JsonSerializer.Deserialize<StopSqlServicePayload>(payloadJson, StrictJson); }
        catch (JsonException) { return (false, "StopSqlService: malformed payload JSON."); }
        if (p is null) return (false, "StopSqlService: payload deserialized to null.");
        if (p.SchemaVersion != 1) return (false, $"StopSqlService: unsupported schemaVersion {p.SchemaVersion}.");

        var host = p.ServerName?.Trim();
        if (string.IsNullOrEmpty(host) || !HostShape.IsMatch(host))
            return (false, "StopSqlService: serverName is missing or not a valid hostname/FQDN.");

        // ── (3) resolve + allow-list the service name ───────────────────────
        var instance = p.InstanceName?.Trim();
        var serviceName = string.IsNullOrEmpty(instance) || instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
            ? "MSSQLSERVER"
            : $"MSSQL${instance}";
        if (!AllowedServiceName.IsMatch(serviceName))
            return (false, $"StopSqlService: '{serviceName}' is not a permitted SQL Server service name — refusing (only MSSQLSERVER / MSSQL$<instance> are allowed).");

        var hostEsc = host.Replace("'", "''");
        var svcEsc = serviceName.Replace("'", "''");

        // ── (2) dry-run vs live ─────────────────────────────────────────────
        // Both paths first read the service state; the live path additionally stops it.
        // -ErrorAction Stop turns a missing service / unreachable host into a nonzero exit.
        string inner = p.DryRun
            ? $"$s = Get-Service -Name '{svcEsc}' -ErrorAction Stop; 'STATE:' + $s.Status"
            : $"$s = Get-Service -Name '{svcEsc}' -ErrorAction Stop; if ($s.Status -ne 'Stopped') {{ Stop-Service -Name '{svcEsc}' -Force -ErrorAction Stop }}; 'STATE:' + (Get-Service -Name '{svcEsc}').Status";
        string script = $"Invoke-Command -ComputerName '{hostEsc}' -ScriptBlock {{ {inner} }} -ErrorAction Stop";

        _logger.LogInformation("StopSqlService {Id}: {Mode} service {Svc} on {Host}",
            commandId, p.DryRun ? "DRY-RUN (report only)" : "LIVE stop", serviceName, host);

        try
        {
            var (exit, stdout, stderr) = await RunPowerShellAsync(script, ct);
            var state = ParseState(stdout);

            if (exit != 0)
            {
                var err = Trim(stderr, 300);
                return (false, $"StopSqlService {(p.DryRun ? "(dry-run)" : "")}: could not reach service '{serviceName}' on {host} — {(string.IsNullOrWhiteSpace(err) ? "no detail" : err)}. Confirm WinRM is enabled on the host and the agent's account can manage services there.");
            }

            if (p.DryRun)
                return (true, $"Dry-run: SQL service '{serviceName}' on {host} is currently {state ?? "unknown"} — a live run would stop it and free its license.");

            var stopped = string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase);
            return stopped
                ? (true, $"SQL service '{serviceName}' on {host} is now Stopped.")
                : (false, $"Issued stop for '{serviceName}' on {host} but its reported state is {state ?? "unknown"} — verify on the host.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StopSqlService {Id}: unexpected failure controlling {Svc} on {Host}", commandId, serviceName, host);
            return (false, $"StopSqlService: {Trim(ex.Message, 200)}");
        }
    }

    private static string? ParseState(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var idx = stdout.LastIndexOf("STATE:", StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = stdout[(idx + 6)..].Trim();
        var nl = tail.IndexOfAny(new[] { '\r', '\n' });
        return nl >= 0 ? tail[..nl].Trim() : tail;
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] : s);

    private static async Task<(int Exit, string StdOut, string StdErr)> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = string.Concat("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"", script.Replace("\"", "\\\""), "\""),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "", "Failed to start powershell.exe on the agent.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, stdout, stderr);
    }
}
