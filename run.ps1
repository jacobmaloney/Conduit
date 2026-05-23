# Conduit Run Script
param(
    [Parameter(Position = 0)]
    [ValidateSet("build", "run", "docker", "docker-dev", "install", "test", "clean")]
    [string]$Command = "run",
    
    [switch]$Release,
    [switch]$Watch
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Text)
    Write-Host "`n$Text" -ForegroundColor Cyan
    Write-Host ("-" * $Text.Length) -ForegroundColor Cyan
}

function Build-Solution {
    Write-Header "Building Conduit"
    
    $configuration = if ($Release) { "Release" } else { "Debug" }
    dotnet build -c $configuration
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Build completed successfully!" -ForegroundColor Green
}

function Run-Application {
    Write-Header "Starting Conduit"
    
    Set-Location "src/Conduit.Web"
    
    if ($Watch) {
        Write-Host "Starting in watch mode (hot reload enabled)..." -ForegroundColor Yellow
        dotnet watch run
    }
    else {
        dotnet run
    }
    
    Set-Location "../.."
}

function Run-Docker {
    Write-Header "Starting Conduit with Docker"
    
    docker-compose up --build
}

function Run-DockerDev {
    Write-Header "Starting Development Database"
    
    docker-compose -f docker-compose.dev.yml up -d
    
    Write-Host "`nDevelopment database started!" -ForegroundColor Green
    Write-Host "Connection string: Server=localhost,1433;Database=Conduit;User Id=sa;Password=Dev!Password123;TrustServerCertificate=True" -ForegroundColor Yellow
    Write-Host "Adminer URL: http://localhost:8080" -ForegroundColor Yellow
}

function Run-Installer {
    Write-Header "Running Conduit Installer"
    
    Set-Location "src/Conduit.Installer"
    dotnet run
    Set-Location "../.."
}

function Run-Tests {
    Write-Header "Running Tests"
    
    dotnet test
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "All tests passed!" -ForegroundColor Green
}

function Clean-Solution {
    Write-Header "Cleaning Solution"
    
    # Clean bin and obj directories
    Get-ChildItem -Path . -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force
    
    # Clean Docker volumes (with confirmation)
    $cleanDocker = Read-Host "Clean Docker volumes? (y/N)"
    if ($cleanDocker -eq 'y') {
        docker-compose down -v
        docker-compose -f docker-compose.dev.yml down -v
    }
    
    Write-Host "Clean completed!" -ForegroundColor Green
}

# Main script execution
switch ($Command) {
    "build" { Build-Solution }
    "run" { 
        Build-Solution
        Run-Application 
    }
    "docker" { Run-Docker }
    "docker-dev" { Run-DockerDev }
    "install" { Run-Installer }
    "test" { Run-Tests }
    "clean" { Clean-Solution }
}

Write-Host "`nDone!" -ForegroundColor Green