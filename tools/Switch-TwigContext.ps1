#!/usr/bin/env pwsh
# ============================================================================
# Switch-TwigContext.ps1
# Patches .twig/config identity fields (organization, project, team) to switch
# the active twig workspace context. Both SQLite databases must already exist
# at .twig/{org}/{project}/twig.db — this script does NOT call twig init.
#
# Usage:
#   .\tools\Switch-TwigContext.ps1 -Org dangreen-msft -Project Twig
#   .\tools\Switch-TwigContext.ps1 -Org microsoft -Project OS -Team CloudVault
#
# Save/Restore:
#   . .\tools\Switch-TwigContext.ps1  # dot-source to import functions
#   Save-TwigConfig                   # backup current config
#   .\tools\Switch-TwigContext.ps1 -Org microsoft -Project OS
#   Restore-TwigConfig                # restore original config
# ============================================================================

param(
    [Parameter(Mandatory = $false)][string]$Org,
    [Parameter(Mandatory = $false)][string]$Project,
    [string]$Team = "",
    [string]$WorkspaceRoot = (Get-Location).Path
)

function Get-TwigConfigPath {
    param([string]$Root)
    return Join-Path $Root ".twig" "config"
}

function Save-TwigConfig {
    <#
    .SYNOPSIS
    Backs up the current .twig/config to a temp file.
    .DESCRIPTION
    Copies .twig/config to a deterministic temp path so it can be restored
    later with Restore-TwigConfig. The backup path is stored in
    $env:TWIG_CONFIG_BACKUP for cross-call access.
    #>
    param([string]$Root = (Get-Location).Path)

    $ErrorActionPreference = 'Stop'

    $configPath = Get-TwigConfigPath -Root $Root
    if (-not (Test-Path $configPath)) {
        Write-Error "No .twig/config found at '$configPath'. Run 'twig init' first." -ErrorAction Continue
        return $false
    }

    $backupDir = Join-Path ([System.IO.Path]::GetTempPath()) "twig-config-backup"
    if (-not (Test-Path $backupDir)) {
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    }

    $backupPath = Join-Path $backupDir "config.bak"

    # Guard against nested saves — refuse to overwrite an existing backup
    if (Test-Path $backupPath) {
        Write-Error "A backup already exists at '$backupPath'. Call Restore-TwigConfig before saving again." -ErrorAction Continue
        return $false
    }

    Copy-Item -Path $configPath -Destination $backupPath -Force
    $env:TWIG_CONFIG_BACKUP = $backupPath

    Write-Host "Saved twig config backup to $backupPath" -ForegroundColor Yellow
    return $true
}

function Restore-TwigConfig {
    <#
    .SYNOPSIS
    Restores .twig/config from the backup created by Save-TwigConfig.
    .DESCRIPTION
    Copies the backup file back to .twig/config, preserving the original
    configuration. Removes the backup file after successful restore.
    #>
    param([string]$Root = (Get-Location).Path)

    $ErrorActionPreference = 'Stop'

    $configPath = Get-TwigConfigPath -Root $Root
    $backupPath = $env:TWIG_CONFIG_BACKUP

    if (-not $backupPath -or -not (Test-Path $backupPath)) {
        Write-Error "No twig config backup found. Run Save-TwigConfig first." -ErrorAction Continue
        return $false
    }

    Copy-Item -Path $backupPath -Destination $configPath -Force
    Remove-Item -Path $backupPath -Force
    $env:TWIG_CONFIG_BACKUP = $null

    Write-Host "Restored twig config from backup" -ForegroundColor Green
    return $true
}

function Switch-TwigContext {
    <#
    .SYNOPSIS
    Switches the twig workspace context by patching .twig/config.
    .DESCRIPTION
    Reads .twig/config as JSON, updates the organization, project, and team
    fields, and writes back with UTF-8 encoding. All other config settings
    are preserved. The target database at .twig/{org}/{project}/twig.db must
    already exist.
    #>
    param(
        [Parameter(Mandatory)][string]$Org,
        [Parameter(Mandatory)][string]$Project,
        [string]$Team = "",
        [string]$Root = (Get-Location).Path
    )

    $ErrorActionPreference = 'Stop'

    # Validate parameters to prevent path traversal
    if ($Org -match '[/\\]|\.\.' -or $Project -match '[/\\]|\.\.') {
        Write-Error "Org and Project must not contain path separators or '..'" -ErrorAction Continue
        return $false
    }

    $configPath = Get-TwigConfigPath -Root $Root
    if (-not (Test-Path $configPath)) {
        Write-Error "No .twig/config found at '$configPath'. Run 'twig init' first." -ErrorAction Continue
        return $false
    }

    # Verify target database exists
    $dbPath = Join-Path $Root ".twig" $Org $Project "twig.db"
    if (-not (Test-Path $dbPath)) {
        Write-Error "No database at '$dbPath'. Run 'twig init --org $Org --project $Project' first." -ErrorAction Continue
        return $false
    }

    $raw = Get-Content $configPath -Raw -Encoding UTF8
    $config = $raw | ConvertFrom-Json

    # True no-op: skip write if values already match (preserves mtime)
    if ([string]($config.organization) -eq $Org -and [string]($config.project) -eq $Project -and [string]($config.team) -eq $Team) {
        Write-Host "Context already set to $Org/$Project$(if ($Team) { "/$Team" }) — no change needed" -ForegroundColor Yellow
        return $true
    }

    $config.organization = $Org
    $config.project = $Project
    $config.team = $Team

    $json = $config | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($configPath, $json, [System.Text.UTF8Encoding]::new($false))

    Write-Host "Switched twig context to $Org/$Project$(if ($Team) { "/$Team" })" -ForegroundColor Green
    return $true
}

# When invoked directly (not dot-sourced), execute the context switch
if ($Org -and $Project) {
    $ErrorActionPreference = 'Stop'
    $result = Switch-TwigContext -Org $Org -Project $Project -Team $Team -Root $WorkspaceRoot
    if (-not $result) { exit 1 }
}
