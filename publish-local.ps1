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
"%~dp0{1}" {2}%*
exit /b %ERRORLEVEL%
'@

function Install-Shim($exeName, $coreName, $entryArguments = '') {
    $shimPath = Join-Path $globalDir "$([System.IO.Path]::GetFileNameWithoutExtension($exeName)).cmd"
    $content  = $shimTemplate -f $exeName, $coreName, $entryArguments
    Set-Content -Path $shimPath -Value $content -Encoding ASCII
    Write-Host "  Installed shim: $shimPath" -ForegroundColor DarkGray
}

function Enable-Shims {
    $releaseCli = Join-Path $globalDir 'twig-core.exe'
    if (-not (Test-Path $releaseCli)) { $releaseCli = Join-Path $globalDir 'twig.exe' }
    $releaseTui = Join-Path $globalDir 'twig-tui-core.exe'
    if (-not (Test-Path $releaseTui)) { $releaseTui = Join-Path $globalDir 'twig-tui.exe' }
    $nativeRelease = (Test-Path $releaseCli) -and (Test-Path $releaseTui) -and
        ((Get-FileHash $releaseCli -Algorithm SHA256).Hash -eq (Get-FileHash $releaseTui -Algorithm SHA256).Hash)
    # Rename release binaries so the .cmd shims take precedence via PATHEXT.
    foreach ($name in @('twig', 'twig-tui', 'twig-bench-tui')) {
        if ($name -ne 'twig' -and -not $nativeRelease) {
            Write-Host "  Retaining existing global $name launch; native aliases are repo-local until the global release is qualified." -ForegroundColor DarkGray
            continue
        }
        $exe  = Join-Path $globalDir "$name.exe"
        $core = Join-Path $globalDir "$name-core.exe"
        if ((Test-Path $exe) -and -not (Test-Path $core)) {
            Rename-Item $exe $core
            Write-Host "  Renamed $name.exe -> $name-core.exe" -ForegroundColor DarkGray
        }
        # Renamed aliases no longer have dispatch basenames. Invoke the release
        # CLI's canonical tui command instead; repo-local aliases launch directly.
        $entryArguments = if ($name -eq 'twig') { '' } else { 'tui ' }
        Install-Shim "$name.exe" 'twig-core.exe' $entryArguments
    }
}

function Disable-Shims([string[]]$Names = @('twig', 'twig-tui', 'twig-bench-tui', 'twig-mcp')) {
    foreach ($name in $Names) {
        $shim = Join-Path $globalDir "$name.cmd"
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

# This directory contains generated publish output. Recreate it so a prior
# Terminal.Gui publish cannot leave a second Windows UI/runtime in the payload.
if (Test-Path $localDir) {
    Remove-Item $localDir -Recurse -Force
}
New-Item -ItemType Directory -Path $localDir -Force | Out-Null

Invoke-Publish "twig" "src\Twig\Twig.csproj"
foreach ($alias in @('twig-tui', 'twig-bench-tui')) {
    Copy-Item (Join-Path $localDir 'twig.exe') (Join-Path $localDir "$alias.exe")
}

# Retire only the old repo-local MCP launcher; preserve any installed release binary.
Disable-Shims @('twig-mcp')
Get-ChildItem -Path $localDir -Filter 'twig-mcp*' -File | Remove-Item -Force

# Install shims so 'twig' on PATH resolves to the local build
Enable-Shims

Write-Host ""
Write-Host "Local publish complete!" -ForegroundColor Green
foreach ($name in @('twig', 'twig-tui', 'twig-bench-tui')) {
    $exe = Join-Path $localDir "$name.exe"
    if (Test-Path $exe) {
        $v = & $exe --version 2>&1
        Write-Host ("  {0,-9} {1}" -f "$($name):", $v)
    }
}
Write-Host "  Build at: $localDir" -ForegroundColor DarkGray
Write-Host "  Run './publish-local.ps1 -Restore' to remove local build and shims." -ForegroundColor DarkGray
