#!/usr/bin/env pwsh
# ============================================================================
# preflight-check-wrapper.ps1
# Test harness that mocks all external commands before running preflight-check.ps1
# Used by preflight-check.Tests.ps1 — not intended for direct invocation.
# ============================================================================
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][int]$WorkItemId
)

$ErrorActionPreference = 'Stop'
$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json

# ── Mock external CLI commands ──────────────────────────────────────────

function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$a)
    $j = $a -join ' '
    if ($j -match 'api user') {
        if ($cfg.GhApiUser -eq '__THROW__') { throw 'gh auth failed' }
        return $cfg.GhApiUser
    }
    if ($j -match 'api repos/') {
        if ($cfg.GhApiRepoPerms -eq '__THROW__') { throw 'gh api failed' }
        return $cfg.GhApiRepoPerms
    }
    if ($j -match 'repo set-default') {
        if ($cfg.GhRepoSetDefault -eq '__THROW__') { throw 'gh repo set-default failed' }
        return $cfg.GhRepoSetDefault
    }
    return ''
}

function git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$a)
    $j = $a -join ' '
    if ($j -match 'remote get-url') {
        if ($cfg.GitRemoteUrl -eq '__THROW__') { throw 'git remote failed' }
        return $cfg.GitRemoteUrl
    }
    if ($j -match 'branch --show-current') {
        if ($cfg.GitBranch -eq '__THROW__') { throw 'git branch failed' }
        return $cfg.GitBranch
    }
    return ''
}

function twig {
    param([Parameter(ValueFromRemainingArguments)][string[]]$a)
    $j = $a -join ' '
    if ($j -match '^sync') {
        if ($cfg.TwigSync -eq '__THROW__') { throw 'twig sync failed' }
        return $cfg.TwigSync
    }
    if ($j -match '^set') {
        if ($cfg.TwigSet -eq '__THROW__') { throw 'twig set failed' }
        return $cfg.TwigSet
    }
    if ($j -match '^state') {
        if ($cfg.TwigStateHelp -eq '__THROW__') { throw 'twig state failed' }
        return $cfg.TwigStateHelp
    }
    return ''
}

function dotnet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$a)
    if ($cfg.DotnetVersion -eq '__THROW__') { throw 'dotnet not found' }
    return $cfg.DotnetVersion
}

function conductor {
    param([Parameter(ValueFromRemainingArguments)][string[]]$a)
    if ($cfg.ConductorVersion -eq '__THROW__') { throw 'conductor not found' }
    return $cfg.ConductorVersion
}

# ── Mock job cmdlets (conductor check uses Start-Job) ───────────────────

function Start-Job {
    [CmdletBinding()]
    param([ScriptBlock]$ScriptBlock)
    $r = $null
    $state = 'Completed'
    try {
        if ($cfg.ConductorVersion -eq '__THROW__') { throw 'conductor not found' }
        $r = $cfg.ConductorVersion
    } catch {
        $state = 'Failed'
    }
    [PSCustomObject]@{ State = $state; Id = 1; Result = $r }
}

function Wait-Job {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)][object]$InputObject, [int]$Timeout)
    process { return $InputObject }
}

function Receive-Job {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline, Position = 0)][object]$InputObject)
    process { return $InputObject.Result }
}

function Stop-Job {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)][object]$InputObject)
    process { }
}

function Remove-Job {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)][object]$InputObject, [switch]$Force)
    process { }
}

# ── Mock discovery/infrastructure cmdlets ───────────────────────────────

function Get-Command {
    [CmdletBinding()]
    param([string]$Name, [string]$ErrorAction2)
    if ($Name -eq 'twig-mcp') {
        if ($cfg.TwigMcpPath -eq '' -or $cfg.TwigMcpPath -eq 'false') { return $null }
        return [PSCustomObject]@{ Source = $cfg.TwigMcpPath }
    }
    return Microsoft.PowerShell.Core\Get-Command $Name -ErrorAction SilentlyContinue
}

function Test-Path {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Path)
    if ($Path -eq '.twig/') { return ($cfg.TwigConfigExists -eq 'true') }
    return (Microsoft.PowerShell.Management\Test-Path $Path)
}

function Invoke-WebRequest {
    [CmdletBinding()]
    param([string]$Method, [string]$Uri, [int]$TimeoutSec, [switch]$UseBasicParsing)
    if ($Uri -match 'dev.azure.com') {
        if ($cfg.AdoNetworkOk -ne 'true') { throw 'Network unreachable' }
        return [PSCustomObject]@{ StatusCode = 200 }
    }
    if ($Uri -match 'github.com') {
        if ($cfg.GhNetworkOk -ne 'true') { throw 'Network unreachable' }
        return [PSCustomObject]@{ StatusCode = 200 }
    }
    throw "Unexpected URL: $Uri"
}

# ── Execute the real script ─────────────────────────────────────────────
& $ScriptPath -WorkItemId $WorkItemId
