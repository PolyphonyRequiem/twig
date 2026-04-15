#!/usr/bin/env pwsh
# Publish twig into a repo-local .local/bin directory for development.
# A shim (twig.cmd) in ~/.twig/bin walks up from CWD to find the local
# build, falling back to the release binary (twig-core.exe) when no
# repo-local override exists.
#
# Usage:
#   ./publish-local.ps1            # Build into .local/bin and install shims
#   ./publish-local.ps1 -Restore   # Remove local build and shims

param(
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$globalDir  = Join-Path $HOME ".twig" "bin"
$localDir   = Join-Path $PSScriptRoot ".local" "bin"
$projectDir = $PSScriptRoot

# --- Shim content ---

$shimTemplate = @'
@echo off
setlocal
rem Walk up from CWD to repo root looking for a local build.
set "dir=%CD%"
:loop
if exist "%dir%\.local\bin\{0}" (
    "%dir%\.local\bin\{0}" %*
    exit /b %ERRORLEVEL%
)
if exist "%dir%\.git" goto fallback
for %%I in ("%dir%\..") do set "parent=%%~fI"
if "%parent%"=="%dir%" goto fallback
set "dir=%parent%"
goto loop
:fallback
"%~dp0{1}" %*
exit /b %ERRORLEVEL%
'@

function Install-Shim($exeName, $coreName) {
    $shimPath = Join-Path $globalDir "$([System.IO.Path]::GetFileNameWithoutExtension($exeName)).cmd"
    $content  = $shimTemplate -f $exeName, $coreName
    Set-Content -Path $shimPath -Value $content -Encoding ASCII
    Write-Host "  Installed shim: $shimPath" -ForegroundColor DarkGray
}

function Enable-Shims {
    # Rename release binaries so the .cmd shims take precedence via PATHEXT.
    foreach ($name in @('twig', 'twig-mcp', 'twig-tui')) {
        if ((Test-Path $exe) -and -not (Test-Path $core)) {
            Rename-Item $exe $core
            Write-Host "  Renamed $name.exe -> $name-core.exe" -ForegroundColor DarkGray
        }
        Install-Shim "$name.exe" "$name-core.exe"
    }
}

function Disable-Shims {
    foreach ($name in @('twig', 'twig-mcp', 'twig-tui')) {
        $core = Join-Path $globalDir "$name-core.exe"
        $exe  = Join-Path $globalDir "$name.exe"
        if (Test-Path $shim) {
            Remove-Item $shim -Force
            Write-Host "  Removed shim: $shim" -ForegroundColor DarkGray
        }
        if ((Test-Path $core) -and -not (Test-Path $exe)) {
            Rename-Item $core $exe
            Write-Host "  Restored $name-core.exe -> $name.exe" -ForegroundColor DarkGray
        }
    }
}

function Invoke-Publish($label, $csproj, $type = "AOT") {
    Write-Host ""
    Write-Host "Publishing $label ($type, win-x64) ..." -ForegroundColor Cyan
    dotnet publish "$projectDir\$csproj" -c Release -r win-x64 --self-contained true -o $localDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "$label publish failed." -ForegroundColor Red
        exit 1
    }
}

# --- Restore ---

if ($Restore) {
    if (Test-Path $localDir) {
        Remove-Item $localDir -Recurse -Force
        Write-Host "Removed local build at $localDir" -ForegroundColor Green
    } else {
        Write-Host "No local build found at $localDir" -ForegroundColor Yellow
    }
    Disable-Shims
    $twigExe = Join-Path $globalDir "twig.exe"
    if (Test-Path $twigExe) {
        $v = & $twigExe --version 2>&1
        Write-Host "Restored to release version: $v" -ForegroundColor Green
    }
    exit 0
}

# --- Build & deploy (repo-local) ---

if (-not (Test-Path $localDir)) {
    New-Item -ItemType Directory -Path $localDir -Force | Out-Null
}

Invoke-Publish "twig" "src\Twig\Twig.csproj"
Invoke-Publish "twig-mcp" "src\Twig.Mcp\Twig.Mcp.csproj"
Invoke-Publish "twig-tui" "src\Twig.Tui\Twig.Tui.csproj" "SingleFile"

# Install shims so 'twig' on PATH resolves to the local build
Enable-Shims

# Install shims so 'twig' on PATH resolves to the local build
Enable-Shims

Write-Host ""
Write-Host "Local publish complete!" -ForegroundColor Green
foreach ($name in @('twig', 'twig-mcp', 'twig-tui')) {
    $exe = Join-Path $localDir "$name.exe"
    if (Test-Path $exe) {
        $v = & $exe --version 2>&1
        Write-Host ("  {0,-9} {1}" -f "$($name):", $v)
    }
}
Write-Host "  Build at: $localDir" -ForegroundColor DarkGray
Write-Host "  Run './publish-local.ps1 -Restore' to remove local build and shims." -ForegroundColor DarkGray
