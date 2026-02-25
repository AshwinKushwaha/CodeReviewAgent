# ?????????????????????????????????????????????????????????????
#  CodeReviewAgent – First-Time Setup Script
#  Run once after cloning: .\setup.ps1
# ?????????????????????????????????????????????????????????????

Write-Host ""
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "   CodeReviewAgent – Setup" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host ""

# ?? 1. Check .NET 10 is installed ?????????????????????????????
Write-Host "Checking .NET version..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith("10.")) {
    Write-Host ""
    Write-Host "ERROR: .NET 10 SDK is required but not found." -ForegroundColor Red
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Red
    Write-Host ""
    exit 1
}
Write-Host ".NET $dotnetVersion detected." -ForegroundColor Green

# ?? 2. Check Git is installed ??????????????????????????????????
Write-Host "Checking Git..." -ForegroundColor Yellow
$gitVersion = git --version 2>$null
if (-not $gitVersion) {
    Write-Host ""
    Write-Host "ERROR: Git is required but not found." -ForegroundColor Red
    Write-Host "Download from: https://git-scm.com/downloads" -ForegroundColor Red
    Write-Host ""
    exit 1
}
Write-Host "$gitVersion detected." -ForegroundColor Green

# ?? 3. Create appsettings.json from template ??????????????????
$settingsSource = "CodeReviewAgent\appsettings.template.json"
$settingsDest   = "CodeReviewAgent\appsettings.json"

if (Test-Path $settingsDest) {
    Write-Host ""
    Write-Host "appsettings.json already exists – skipping copy." -ForegroundColor DarkGray
} else {
    Copy-Item $settingsSource $settingsDest
    Write-Host ""
    Write-Host "Created appsettings.json from template." -ForegroundColor Green
}

# ?? 4. Prompt user to enter GitHub token ??????????????????????
$settings = Get-Content $settingsDest -Raw | ConvertFrom-Json
if ($settings.GitHubToken -eq "YOUR_GITHUB_PERSONAL_ACCESS_TOKEN_HERE") {
    Write-Host ""
    Write-Host "A GitHub Personal Access Token is required to call GitHub Models." -ForegroundColor Yellow
    Write-Host "Generate one at: https://github.com/settings/tokens" -ForegroundColor Yellow
    Write-Host "(Ensure your account has GitHub Copilot access)" -ForegroundColor Yellow
    Write-Host ""
    $token = Read-Host "Paste your GitHub Personal Access Token"

    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Host ""
        Write-Host "WARNING: No token entered. You must set GitHubToken in appsettings.json manually before running." -ForegroundColor DarkYellow
    } else {
        $settings.GitHubToken = $token
        $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsDest
        Write-Host "Token saved to appsettings.json." -ForegroundColor Green
    }
}

# ?? 5. Restore NuGet packages ?????????????????????????????????
Write-Host ""
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
Push-Location "CodeReviewAgent"
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Package restore failed." -ForegroundColor Red
    Pop-Location
    exit 1
}
Pop-Location
Write-Host "Packages restored." -ForegroundColor Green

# ?? 6. Build the project ??????????????????????????????????????
Write-Host ""
Write-Host "Building project..." -ForegroundColor Yellow
Push-Location "CodeReviewAgent"
dotnet build --no-restore -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed." -ForegroundColor Red
    Pop-Location
    exit 1
}
Pop-Location
Write-Host "Build successful." -ForegroundColor Green

# ?? 7. Done ???????????????????????????????????????????????????
Write-Host ""
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "   Setup complete!" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Run a review with:" -ForegroundColor White
Write-Host ""
Write-Host "  cd CodeReviewAgent" -ForegroundColor Green
Write-Host "  dotnet run -- ``" -ForegroundColor Green
Write-Host "    --repoPath  `"C:\path\to\your\repo`" ``" -ForegroundColor Green
Write-Host "    --sourceBranch `"feature-branch`" ``" -ForegroundColor Green
Write-Host "    --targetBranch `"main`" ``" -ForegroundColor Green
Write-Host "    --rulesPath    `".\rules.md`"" -ForegroundColor Green
Write-Host ""
Write-Host "Reports are saved to: CodeReviewAgent\ReviewReports\" -ForegroundColor DarkGray
Write-Host ""
