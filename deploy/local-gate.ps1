<#
.SYNOPSIS
    Conduit's CI build gate, run locally before a push: the same restore, Release build with warnings as errors, and
    tests as .github/workflows/build.yml.
.DESCRIPTION
    A Debug build or a test-only build is not this gate. On 2026-11-06 a Blazor analyzer warning (BL0005) in a new
    test passed every local run and failed CI, which builds Release with /warnaserror.
    The restore re-evaluates floating package versions (8.0.*), as CI's fresh restore does. Without it, a local
    package cache holding two patch levels of one package (JwtBearer 8.0.32 and 8.0.33 on 2026-11-06) fails with
    MSB3277, an error CI never sees. Do not silence that with /nowarn; restore the way CI does.
.EXAMPLE
    .\deploy\local-gate.ps1              # restore, Release /warnaserror build, all tests
    .\deploy\local-gate.ps1 -SkipTests   # the build gate only
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\Conduit.sln' | Resolve-Path
$started = Get-Date

Write-Host '==> restore (floating versions re-evaluated, as a fresh CI restore resolves them)'
dotnet restore $solution --force-evaluate -v q
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Host '==> build Release, warnings as errors (CI: Build (Release, warnings-as-errors))'
dotnet build $solution --configuration Release --no-restore /warnaserror -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Release build with warnings as errors failed: CI fails the same way. Fix the warning; do not suppress it globally.' }

if (-not $SkipTests) {
    Write-Host '==> test (CI: dotnet test Release, no build)'
    dotnet test $solution --configuration Release --no-build --no-restore -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host ("PASS: Conduit CI gate reproduced locally in {0:N0}s." -f ((Get-Date) - $started).TotalSeconds)
