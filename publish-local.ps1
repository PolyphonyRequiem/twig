#!/usr/bin/env pwsh
# Publish twig directly into ~/.twig/bin for local development.
# Usage:
#   ./publish-local.ps1            # Build and deploy
#   ./publish-local.ps1 -Restore   # Roll back to previous binary

param(
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $HOME ".twig" "bin"
$backupDir  = Join-Path $HOME ".twig" "bin.bak"
$twigExe    = Join-Path $installDir "twig.exe"
$projectDir = $PSScriptRoot

if ($Restore) {
    if (-not (Test-Path $backupDir)) {
        Write-Host "No backup found at $backupDir — nothing to restore." -ForegroundColor Yellow
        exit 1
    }
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Rename-Item $backupDir $installDir
    Write-Host "Restored previous twig from backup." -ForegroundColor Green
    if (Test-Path $twigExe) {
        $v = & $twigExe --version 2>&1
        Write-Host "  $v"
    }
    exit 0
}

# --- Build & deploy ---

Write-Host "Publishing twig (AOT, win-x64) ..." -ForegroundColor Cyan

# Back up current install
if (Test-Path $installDir) {
    if (Test-Path $backupDir) { Remove-Item $backupDir -Recurse -Force }
    Copy-Item $installDir $backupDir -Recurse
    Write-Host "Backed up current install to $backupDir"
}

# Publish directly into the install directory
dotnet publish "$projectDir\src\Twig\Twig.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $installDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed. Restoring backup..." -ForegroundColor Red
    if (Test-Path $backupDir) {
        if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
        Rename-Item $backupDir $installDir
        Write-Host "Restored previous version from backup." -ForegroundColor Yellow
    }
    exit 1
}

Write-Host ""
Write-Host "Local publish complete!" -ForegroundColor Green
if (Test-Path $twigExe) {
    $v = & $twigExe --version 2>&1
    Write-Host "  $v"
}
Write-Host "  Run './publish-local.ps1 -Restore' to roll back." -ForegroundColor DarkGray
