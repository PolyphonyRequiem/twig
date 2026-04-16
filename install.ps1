#!/usr/bin/env pwsh
# Twig CLI installer for Windows
# Usage: irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'

$repo = "PolyphonyRequiem/twig"
$assetName = "twig-win-x64.zip"
$installDir = Join-Path $HOME ".twig" "bin"

Write-Host "Installing twig..." -ForegroundColor Cyan

# Query GitHub Releases API for latest release
try {
    $releaseUrl = "https://api.github.com/repos/$repo/releases/latest"
    $headers = @{ 'User-Agent' = 'twig-installer' }
    if ($env:GITHUB_TOKEN) {
        $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
    }
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers
} catch {
    # Check for GitHub API rate limiting (HTTP 403)
    $errorMessage = "$($_.ErrorDetails.Message) $($_.Exception.Message)"
    if ($errorMessage -match 'API rate limit exceeded') {
        if ($env:GITHUB_TOKEN) {
            Write-Host "Error: GitHub API rate limit exceeded even with GITHUB_TOKEN. Try again later." -ForegroundColor Red
        } else {
            Write-Host "Error: GitHub API rate limit exceeded. Try again later or set the GITHUB_TOKEN environment variable to a GitHub personal access token." -ForegroundColor Red
        }
        exit 1
    }
    Write-Host "Error: Failed to query GitHub Releases API. Check your internet connection." -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    exit 1
}

# Find the matching asset
$asset = $release.assets | Where-Object { $_.name -eq $assetName }
if (-not $asset) {
    Write-Host "Error: Asset '$assetName' not found in release $($release.tag_name)." -ForegroundColor Red
    Write-Host "Available assets: $($release.assets.name -join ', ')" -ForegroundColor Red
    exit 1
}

$downloadUrl = $asset.browser_download_url
Write-Host "Downloading twig $($release.tag_name)..."

# Download to temp
$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) $assetName
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip -UseBasicParsing
} catch {
    Write-Host "Error: Failed to download $downloadUrl" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    exit 1
}

# Create install directory
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Write-Host "Created $installDir"
}

# Extract twig.exe
try {
    Expand-Archive -Path $tempZip -DestinationPath $installDir -Force
} catch {
    Write-Host "Error: Failed to extract archive." -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item -Path $tempZip -Force -ErrorAction SilentlyContinue
}

# Verify primary binary exists
$twigExe = Join-Path $installDir "twig.exe"
if (-not (Test-Path $twigExe)) {
    Write-Host "Error: twig.exe not found after extraction." -ForegroundColor Red
    exit 1
}

# Verify companion binaries (warn only — older archives may not include them)
foreach ($companion in @("twig-mcp.exe", "twig-tui.exe")) {
    if (Test-Path (Join-Path $installDir $companion)) {
        Write-Host "  Found $companion" -ForegroundColor Green
    } else {
        Write-Warning "$companion not found in archive. Some features may be unavailable. Run 'twig upgrade' after install to fetch companions."
    }
}

# Add to user PATH if not already present
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -split ';' | Where-Object { $_ -eq $installDir }) {
    Write-Host "PATH already contains $installDir"
} else {
    $newPath = if ($userPath) { "$userPath;$installDir" } else { $installDir }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Host "Added $installDir to user PATH"
}

# Also add to current session PATH so twig works immediately
if ($env:Path -split ';' | Where-Object { $_ -eq $installDir }) {
    # Already in current session
} else {
    $env:Path = "$installDir;$env:Path"
}

# Print version
Write-Host ""
Write-Host "twig installed successfully!" -ForegroundColor Green
try {
    $version = & $twigExe --version 2>&1
    Write-Host "  $version"
} catch {
    Write-Host "  (installed to $twigExe)"
}
Write-Host ""
Write-Host "twig is available in this session. New terminals will also have twig in PATH automatically." -ForegroundColor Yellow
