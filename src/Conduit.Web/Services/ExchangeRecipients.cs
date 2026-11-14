using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conduit.Connectors.ActiveDirectory;

namespace Conduit.Web.Services;

/// <summary>
/// EXCHANGE-RECIPIENTS-01 (IdentityCenter UC017-S05/S07/S12; Jacob 2026-11-14: "Exchange cmdlets", the supported way).
/// The three Exchange actions the agent performs, each through Microsoft's own recipient-management cmdlets from the
/// Exchange Management Tools (Enable-RemoteMailbox, Set-RemoteMailbox -Type Shared, Enable-DistributionGroup), never by
/// writing msExch* values by hand. The cmdlets run as this service's own identity, which the customer grants Exchange
/// recipient management (Microsoft's Add-PermissionForEMT.ps1 and its "Recipient Management EMT" group).
/// </summary>
public static class ExchangeRecipientOperations
{
    public const string EnableRemoteMailbox = "EnableRemoteMailbox";
    public const string ConvertRemoteMailboxToShared = "ConvertRemoteMailboxToShared";
    public const string MailEnableGroup = "MailEnableGroup";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EnableRemoteMailbox, ConvertRemoteMailboxToShared, MailEnableGroup
    };
}

/// <summary>What the runner is asked to do: one operation on one object (by objectGUID), with its validated inputs.</summary>
public sealed record ExchangeRecipientRequest(
    string Operation, string ObjectGuid, string? Alias, string? PrimarySmtpAddress, string? RemoteRoutingAddress);

/// <summary>What the cmdlet reported: the recipient type it left the object as, and its primary address.</summary>
public sealed record ExchangeRecipientResult(bool Ok, string? RecipientTypeDetails, string? PrimarySmtpAddress, string? Error);

/// <summary>
/// The pure half: the operation against the object's AD-read class and the inputs' shapes. A refusal names the reason
/// before anything runs; the cmdlet itself still refuses what Exchange does not allow (a non-universal group, a user that
/// is already mail-enabled) and that message is returned as is.
/// </summary>
public static class ExchangeRecipientPlan
{
    private static readonly Regex AliasShape = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex SmtpShape = new(@"^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$", RegexOptions.CultureInvariant);

    public static bool IsSmtpAddress(string? value) => value is { Length: <= 254 } && SmtpShape.IsMatch(value);

    public static (ExchangeRecipientRequest? Request, string? Refusal) Build(
        string operation, AdResolvedObject target, string objectGuid,
        string? alias, string? primarySmtpAddress, string? remoteRoutingAddress)
    {
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        primarySmtpAddress = string.IsNullOrWhiteSpace(primarySmtpAddress) ? null : primarySmtpAddress.Trim();
        remoteRoutingAddress = string.IsNullOrWhiteSpace(remoteRoutingAddress) ? null : remoteRoutingAddress.Trim();

        if (!ExchangeRecipientOperations.All.Contains(operation))
            return (null, $"'{operation}' is not an Exchange recipient operation.");
        if (!Guid.TryParse(objectGuid, out var guid) || guid == Guid.Empty)
            return (null, "the object's GUID is missing.");

        var wantsGroup = operation == ExchangeRecipientOperations.MailEnableGroup;
        if (wantsGroup && !target.IsClass("group"))
            return (null, "only a group can be mail-enabled.");
        if (!wantsGroup && (!target.IsClass("user") || target.IsClass("computer")))
            return (null, "a remote mailbox belongs to a user account.");

        if (alias is not null && !AliasShape.IsMatch(alias))
            return (null, "the alias may hold letters, digits, dots, hyphens and underscores (up to 64), starting with a letter or digit.");
        if (primarySmtpAddress is not null && !IsSmtpAddress(primarySmtpAddress))
            return (null, $"'{primarySmtpAddress}' is not an e-mail address.");

        if (operation == ExchangeRecipientOperations.EnableRemoteMailbox)
        {
            if (remoteRoutingAddress is null)
                return (null, "a remote mailbox needs its routing address (alias@<tenant>.mail.onmicrosoft.com).");
            if (!IsSmtpAddress(remoteRoutingAddress))
                return (null, $"'{remoteRoutingAddress}' is not a routing address.");
        }
        else
        {
            remoteRoutingAddress = null;
            if (operation == ExchangeRecipientOperations.ConvertRemoteMailboxToShared) { alias = null; primarySmtpAddress = null; }
        }

        return (new ExchangeRecipientRequest(operation, guid.ToString("D"), alias, primarySmtpAddress, remoteRoutingAddress), null);
    }
}

public interface IExchangeRecipientRunner
{
    Task<ExchangeRecipientResult> RunAsync(ExchangeRecipientRequest request, CancellationToken ct);
}

/// <summary>
/// Runs a request in Windows PowerShell 5.1 (the Exchange snap-in does not load in PowerShell 7) with a FIXED script.
/// The request travels as JSON on standard input and reaches the cmdlets only as named parameter values, so nothing the
/// request holds is ever parsed as script. One JSON line comes back on standard output.
/// </summary>
public sealed class PowerShellExchangeRecipientRunner : IExchangeRecipientRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private readonly ILogger<PowerShellExchangeRecipientRunner> _logger;

    public PowerShellExchangeRecipientRunner(ILogger<PowerShellExchangeRecipientRunner> logger) => _logger = logger;

    /// <summary>The script, kept public so a test can pin that it reads its inputs as data only.</summary>
    public const string Script = @"
$ErrorActionPreference = 'Stop'
try {
    $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
    Add-PSSnapin Microsoft.Exchange.Management.PowerShell.RecipientManagement
    $r = $null
    switch ($in.operation) {
        'EnableRemoteMailbox' {
            $p = @{ Identity = [string]$in.objectGuid; RemoteRoutingAddress = [string]$in.remoteRoutingAddress }
            if ($in.alias) { $p.Alias = [string]$in.alias }
            if ($in.primarySmtpAddress) { $p.PrimarySmtpAddress = [string]$in.primarySmtpAddress }
            Enable-RemoteMailbox @p -Confirm:$false | Out-Null
            $r = Get-RemoteMailbox -Identity ([string]$in.objectGuid)
        }
        'ConvertRemoteMailboxToShared' {
            Set-RemoteMailbox -Identity ([string]$in.objectGuid) -Type Shared -Confirm:$false
            $r = Get-RemoteMailbox -Identity ([string]$in.objectGuid)
        }
        'MailEnableGroup' {
            $p = @{ Identity = [string]$in.objectGuid }
            if ($in.alias) { $p.Alias = [string]$in.alias }
            if ($in.primarySmtpAddress) { $p.PrimarySmtpAddress = [string]$in.primarySmtpAddress }
            Enable-DistributionGroup @p -Confirm:$false | Out-Null
            $r = Get-DistributionGroup -Identity ([string]$in.objectGuid)
        }
        default { throw 'unknown operation' }
    }
    [pscustomobject]@{ ok = $true; recipientTypeDetails = [string]$r.RecipientTypeDetails; primarySmtpAddress = [string]$r.PrimarySmtpAddress } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{ ok = $false; error = [string]$_.Exception.Message } | ConvertTo-Json -Compress
}
";

    public async Task<ExchangeRecipientResult> RunAsync(ExchangeRecipientRequest request, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                        + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ExchangeRecipientResult(false, null, null, $"Windows PowerShell could not be started: {ex.Message}");
        }

        var input = JsonSerializer.Serialize(new
        {
            operation = request.Operation,
            objectGuid = request.ObjectGuid,
            alias = request.Alias,
            primarySmtpAddress = request.PrimarySmtpAddress,
            remoteRoutingAddress = request.RemoteRoutingAddress,
        });
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ExchangeRecipientResult(false, null, null, $"the Exchange cmdlet did not finish within {Timeout.TotalMinutes:0} minutes.");
        }

        return Parse(await stdout, await stderr, _logger);
    }

    /// <summary>The last JSON line on standard output; anything else is reported as a failure with what was printed.</summary>
    public static ExchangeRecipientResult Parse(string stdout, string stderr, ILogger? logger = null)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            logger?.LogWarning("Exchange recipient runner returned no result line; stderr: {Stderr}", stderr.Length > 500 ? stderr[..500] : stderr);
            var said = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return new ExchangeRecipientResult(false, null, null, string.IsNullOrEmpty(said) ? "the Exchange cmdlet returned nothing." : Truncate(said));
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return ok
                ? new ExchangeRecipientResult(true, Str("recipientTypeDetails"), Str("primarySmtpAddress"), null)
                : new ExchangeRecipientResult(false, null, null, Truncate(Str("error") ?? "the Exchange cmdlet failed."));
        }
        catch (JsonException)
        {
            return new ExchangeRecipientResult(false, null, null, Truncate(line));
        }
    }

    private static string Truncate(string s) => s.Length > 400 ? s[..400] : s;
}
