# restore.ps1
# Restores all NuGet packages for the Agiliz solution and installs Playwright browsers.
# Run from the root of the repository: .\restore.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ">> $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "   OK   $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "   FAIL $msg" -ForegroundColor Red
}

# 1. Verify .NET SDK
Write-Step "Checking .NET SDK..."
try {
    $sdk = (dotnet --version 2>&1).Trim()
    if (-not $sdk.StartsWith("10.")) {
        Write-Fail "Found .NET $sdk -- this project requires .NET 10."
        exit 1
    }
    Write-Ok ".NET SDK $sdk"
} catch {
    Write-Fail "dotnet not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download"
    exit 1
}

# 2. Locate solution file
Write-Step "Locating solution..."
$sln = Get-ChildItem -Filter "*.sln" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $sln) {
    Write-Fail "No .sln file found. Run this script from the repository root."
    exit 1
}
Write-Ok $sln.Name

# 3. Restore NuGet packages
Write-Step "Restoring NuGet packages for all projects..."
dotnet restore $sln.FullName
if ($LASTEXITCODE -ne 0) {
    Write-Fail "dotnet restore failed."
    exit 1
}
Write-Ok "All packages restored."

# 4. Build (validates packages resolved correctly)
Write-Step "Building solution (Release)..."
dotnet build $sln.FullName -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Build failed -- check the errors above."
    exit 1
}
Write-Ok "Build succeeded."

# 5. Install Playwright browsers (for E2E tests)
Write-Step "Installing Playwright browsers..."
$playwrightScript = Get-ChildItem -Recurse -Filter "playwright.ps1" `
    -Path "Agiliz.Tests" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($playwrightScript) {
    & powershell $playwrightScript.FullName install chromium
    if ($LASTEXITCODE -ne 0) {
        Write-Host "   WARN Playwright browser install failed. E2E tests will not run." -ForegroundColor Yellow
    } else {
        Write-Ok "Playwright Chromium installed."
    }
} else {
    Write-Host "   SKIP Playwright script not found (run the script again after first build)." -ForegroundColor Yellow
}

# 6. Summary
Write-Host ""
Write-Host "-----------------------------------------" -ForegroundColor DarkGray
Write-Host " Setup complete. Next steps:" -ForegroundColor White
Write-Host ""
Write-Host "   1. Copy and fill in credentials:" -ForegroundColor Gray
Write-Host "      cp .env.example .env" -ForegroundColor DarkGray
Write-Host ""
Write-Host "   2. Create your first bot:" -ForegroundColor Gray
Write-Host "      dotnet run --project Agiliz.CLI -- create my-bot" -ForegroundColor DarkGray
Write-Host ""
Write-Host "   3. Start the services:" -ForegroundColor Gray
Write-Host "      dotnet run --project Agiliz.Runtime   # terminal 1" -ForegroundColor DarkGray
Write-Host "      dotnet run --project Agiliz.Wizard    # terminal 2" -ForegroundColor DarkGray
Write-Host ""
Write-Host "   4. Run tests:" -ForegroundColor Gray
Write-Host "      dotnet test --filter Category!=E2E" -ForegroundColor DarkGray
Write-Host "-----------------------------------------" -ForegroundColor DarkGray
