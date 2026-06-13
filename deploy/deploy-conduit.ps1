<#
.SYNOPSIS
    Safe deploy of the Conduit publish folder to the .56 server as the 'Conduit' Windows service.
    Mirrors the staged publish output to the remote publish root WITHOUT deleting
    runtime-written directories (Logs, App_Data, ...), then restarts the service and
    health-checks it.

.DESCRIPTION
    The deploy uses 'robocopy /MIR' so the server exactly matches the local publish
    output -- stale build files are removed. The danger of /MIR is that it ALSO deletes
    server-only directories the running app created at runtime. This script excludes those
    runtime dirs by FULL REMOTE PATH (/XD) so only the publish-root copies are protected,
    not same-named directories deeper in the tree.

    Runtime-written directories under the publish root (the /XD exclude set):
      * App_Data -> DataProtection keyring lives at App_Data\dp-keys (relative to ContentRoot,
                    which UseWindowsService() pins to the exe dir = publish root). MUST NOT be
                    wiped on redeploy or the server regenerates its keys.
      * Logs     -> ApplicationLogService writes to <CWD>\Logs; under the service CWD is the
                    publish root, so the service creates publish\Logs at runtime.
      * log, uploads, temp -> defensive conventional runtime dir names.

    The PORTABLE credential keyring (C:\ProgramData\Conduit\credential.key) is a flat
    base64 AES key that lives OUTSIDE the publish root and is never touched by this deploy.
    It must be copied to the server out of band (see deploy notes); without it every stored
    ConnectionCredential fails to decrypt.

.NOTES
    No secrets are stored in this file. Pass the SMB credential via -Credential, or
    -SmbUser/-SmbPassword, or be prompted. ASCII-only on purpose: PowerShell 5.1 reads
    a UTF-8 file as Windows-1252, so any em-dash / smart-quote / box-drawing char would
    mojibake and can break parsing.

.EXAMPLE
    # Real deploy with default targets, prompted for the SMB password:
    .\deploy-conduit.ps1 -SmbUser "domain\administrator"

.EXAMPLE
    # Dry run -- proves the excludes without stopping the service or copying anything:
    .\deploy-conduit.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Server      = "192.168.1.56",
    [string]$ServiceName = "Conduit",
    [string]$PublishDir  = "C:\Users\jacob\source\repos\_deploy-conduit\publish",
    [string]$RemotePath  = "\\192.168.1.56\C$\Software\Conduit\publish",
    [int]$Port           = 5500,

    # SMB credentials for the C$ admin share. Provide ONE of: -Credential, or
    # -SmbUser (+ -SmbPassword), else you will be prompted. Nothing is hardcoded.
    [System.Management.Automation.PSCredential]$Credential,
    [string]$SmbUser,
    [string]$SmbPassword,

    # Dry run: robocopy in /L list-only mode, NO service stop/start, NO file changes.
    [switch]$DryRun,

    # Seconds to wait for the service health endpoint to return HTTP 200 after start.
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

# -- Runtime-written dirs to protect from /MIR delete. Names are joined to the remote
#    publish root below so the /XD match is the publish-root copy ONLY, by full path. --
$RuntimeDirNames = @("App_Data", "Logs", "log", "uploads", "temp")

function Write-Step  { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    { param([string]$m) Write-Host "    OK: $m" -ForegroundColor Green }
function Write-Warn2 { param([string]$m) Write-Host "    WARN: $m" -ForegroundColor Yellow }
function Write-Err2  { param([string]$m) Write-Host "    ERROR: $m" -ForegroundColor Red }

$healthUrl = "http://${Server}:$Port/"

Write-Host ""
Write-Host ("=" * 64) -ForegroundColor Cyan
Write-Host " Conduit deploy" -ForegroundColor Cyan
Write-Host ("=" * 64) -ForegroundColor Cyan
Write-Host "  Server      : $Server"
Write-Host "  Service     : $ServiceName"
Write-Host "  PublishDir  : $PublishDir"
Write-Host "  RemotePath  : $RemotePath"
Write-Host "  Port / URL  : $Port  ($healthUrl)"
Write-Host "  Mode        : $(if ($DryRun) { 'DRY RUN (no service stop/start, list-only copy)' } else { 'LIVE DEPLOY' })" -ForegroundColor $(if ($DryRun) { 'Yellow' } else { 'Green' })
Write-Host ""

# -- 1. Verify the local publish is truly self-contained ----------------------------
Write-Step "Verifying publish is self-contained"
if (-not (Test-Path $PublishDir)) {
    Write-Err2 "PublishDir not found: $PublishDir"
    exit 1
}
if (-not (Test-Path (Join-Path $PublishDir "coreclr.dll"))) {
    Write-Err2 "coreclr.dll NOT found in $PublishDir -- this is not a self-contained publish. ABORTING."
    Write-Err2 "Publish with:  dotnet publish ... --self-contained true -r win-x64"
    exit 1
}
Write-Ok "coreclr.dll present (self-contained)."

# -- 2. Establish SMB to the C$ admin share -----------------------------------------
$smbRoot = "\\$Server\C$"
Write-Step "Establishing SMB session to $smbRoot"
# Drop any existing mapping so a stale/expired session does not mask a cred problem.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& net use $smbRoot /delete /y 2>$null | Out-Null
$ErrorActionPreference = $prevEAP

if (-not $Credential) {
    if ($SmbUser -and $SmbPassword) {
        $sec = ConvertTo-SecureString $SmbPassword -AsPlainText -Force
        $Credential = New-Object System.Management.Automation.PSCredential($SmbUser, $sec)
    }
    elseif ($SmbUser) {
        $Credential = Get-Credential -UserName $SmbUser -Message "SMB password for $SmbUser on $Server"
    }
    else {
        $Credential = Get-Credential -Message "SMB credentials for $smbRoot (e.g. domain\administrator)"
    }
}
$plainPwd = $Credential.GetNetworkCredential().Password
$netUser  = $Credential.UserName

# From the SMB connect onward, run inside try/finally so the authenticated C$ mapping is
# ALWAYS torn down and the plaintext password ALWAYS cleared on every exit path.
try {
& net use $smbRoot $plainPwd /user:$netUser | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $RemotePath)) {
    # The publish root may not exist on a first-ever deploy. Try to create it.
    $remoteRoot = "\\$Server\C$\Software\Conduit\publish"
    if (-not (Test-Path "\\$Server\C$")) {
        Write-Err2 "Could not access \\$Server\C$ over SMB. Check credentials / .56 reachability (the VM may have slept -- retry)."
        exit 1
    }
    if (-not (Test-Path $RemotePath)) {
        Write-Warn2 "Remote publish root does not exist yet; creating $RemotePath (first deploy)."
        New-Item -ItemType Directory -Path $RemotePath -Force | Out-Null
    }
}
Write-Ok "SMB session established; remote publish root reachable."

# -- 3. Build the /XD exclude list as FULL REMOTE PATHS -----------------------------
$xdPaths = $RuntimeDirNames | ForEach-Object { Join-Path $RemotePath $_ }
Write-Step "Runtime dirs excluded from /MIR delete (by full remote path):"
foreach ($p in $xdPaths) {
    $exists = if (Test-Path $p) { "present on server" } else { "not present (defensive)" }
    Write-Host "      $p   [$exists]"
}

# -- 4. Stop the service (skipped on dry run; tolerant of a not-yet-created service) -
if (-not $DryRun) {
    Write-Step "Stopping service '$ServiceName' on $Server (tolerant if not yet created)"
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & sc.exe "\\$Server" stop $ServiceName 2>$null | Out-Null
    $ErrorActionPreference = $prevEAP
    # Poll until STOPPED or the service does not exist (first deploy). sc.exe query of a
    # missing service returns a 'does not exist' line -- treat that as 'safe to copy'.
    $stopped = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $q = & sc.exe "\\$Server" query $ServiceName 2>$null
        if ($q -match "STOPPED") { $stopped = $true; break }
        if ($q -match "1060" -or $q -match "does not exist") { $stopped = $true; Write-Ok "Service not yet created (first deploy)."; break }
    }
    if ($stopped) { Write-Ok "Service stopped (or absent)." }
    else { Write-Warn2 "Service did not report STOPPED within 30s; continuing (file locks may cause robocopy retries)." }
}
else {
    Write-Warn2 "DryRun: NOT stopping the service."
}

# -- 5. Mirror with /MIR, excluding the runtime dirs --------------------------------
Write-Step "Mirroring publish -> server"
$roArgs = @($PublishDir, $RemotePath, "/MIR", "/NJH", "/NJS", "/NP", "/FP", "/R:2", "/W:2")
$roArgs += "/XD"
$roArgs += $xdPaths
if ($DryRun) { $roArgs += "/L" }

Write-Host "    robocopy $($roArgs -join ' ')" -ForegroundColor DarkGray
& robocopy @roArgs
$roExit = $LASTEXITCODE
# robocopy exit codes 0-7 are success (8+ are failures).
if ($roExit -ge 8) {
    Write-Err2 "robocopy reported a failure (exit $roExit)."
    if (-not $DryRun) {
        Write-Warn2 "Attempting to restart the service before exiting."
        & sc.exe "\\$Server" start $ServiceName 2>$null | Out-Null
    }
    exit 1
}
Write-Ok "robocopy completed (exit $roExit)."

if ($DryRun) {
    Write-Host ""
    Write-Host ("=" * 64) -ForegroundColor Yellow
    Write-Host " DRY RUN complete. No service action and no files changed." -ForegroundColor Yellow
    Write-Host ("=" * 64) -ForegroundColor Yellow
    exit 0
}

# -- 6. Start the service (tolerant if the service has not been created yet) ---------
Write-Step "Starting service '$ServiceName' on $Server"
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$startOut = & sc.exe "\\$Server" start $ServiceName 2>$null
$ErrorActionPreference = $prevEAP
if ($startOut -match "1060" -or $startOut -match "does not exist") {
    Write-Warn2 "Service '$ServiceName' does not exist on $Server yet. Files are mirrored; create the service with sc.exe, set its Environment, then re-run or start manually."
    Write-Warn2 "Skipping health check (no service to start)."
    exit 0
}
Write-Ok "Start command issued."

# -- 7. Poll health -----------------------------------------------------------------
Write-Step "Polling $healthUrl for HTTP 200 (timeout ${HealthTimeoutSeconds}s)"
$healthy = $false
$deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    try {
        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) { $healthy = $true; break }
    }
    catch {
        # Service still warming up (DB connect / DI graph). Keep polling.
    }
    Start-Sleep -Seconds 3
}

Write-Host ""
Write-Host ("=" * 64) -ForegroundColor Cyan
if ($healthy) {
    Write-Host " DEPLOY SUCCESS" -ForegroundColor Green
    Write-Host " $ServiceName on $Server responded 200 at $healthUrl" -ForegroundColor Green
    Write-Host ("=" * 64) -ForegroundColor Cyan
    exit 0
}
else {
    Write-Host " DEPLOY FAILED HEALTH CHECK" -ForegroundColor Red
    Write-Host " Files were mirrored and the service was started, but $healthUrl did not" -ForegroundColor Red
    Write-Host " return 200 within ${HealthTimeoutSeconds}s. Check the service + logs on ${Server}:" -ForegroundColor Red
    Write-Host "   sc.exe \\$Server query $ServiceName" -ForegroundColor Red
    Write-Host "   $RemotePath\Logs\  (ApplicationLogService file sink)" -ForegroundColor Red
    Write-Host ("=" * 64) -ForegroundColor Cyan
    exit 1
}
}
finally {
    # Always tear down the authenticated C$ mapping and scrub the plaintext password.
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & net use $smbRoot /delete /y 2>$null | Out-Null
    $ErrorActionPreference = $prevEAP

    $plainPwd = $null
    if ($Credential) { $Credential = $null }
}
