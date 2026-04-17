#!/usr/bin/env pwsh
# Validates that --flag tokens used in twig command examples match the flags
# listed in each command's Options section.
# Exit 0: clean pass. Exit 1: mismatches found.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Building twig..." -ForegroundColor Cyan
$buildOutput = & dotnet build "$repoRoot/src/Twig" --nologo -v quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED — cannot validate examples without a working binary." -ForegroundColor Red
    $buildOutput | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "Build succeeded." -ForegroundColor Green

$helpOutput = & twig --help 2>&1 | Out-String
$lines = $helpOutput -split "`n"

$categoryPattern = '^\w[\w\s]*:$'
$commandPattern  = '^\s{2}(\S+(?:\s\S+)*)\s{2,}'
$flagPattern     = '(--[a-z][a-z0-9-]*)'

$commands = [System.Collections.Generic.HashSet[string]]::new()
$inCategory = $false

foreach ($line in $lines) {
    $trimmed = $line.TrimEnd()

    if ($trimmed -match $categoryPattern) {
        $inCategory = $true
        continue
    }

    if ($inCategory -and $trimmed -match $commandPattern) {
        $bare = ($Matches[1] -replace '<[^>]+>' -replace '\[[^\]]+\]' -replace '--[a-z][a-z0-9-]*' -replace '\s+', ' ').Trim()
        if ($bare -ne '') { [void]$commands.Add($bare) }
    }
    elseif ($inCategory -and $trimmed -match '^\S') {
        # Non-indented, non-category line — end of category block
        $inCategory = $false
    }
}

Write-Host "Discovered $($commands.Count) commands." -ForegroundColor Cyan

$mismatches = @()

function Get-SectionFlags([string[]]$lines, [string]$section) {
    $flags = [System.Collections.Generic.HashSet[string]]::new()
    $inSection = $false
    foreach ($cl in $lines) {
        $ct = $cl.TrimEnd()
        if ($ct -match "^${section}:") { $inSection = $true; continue }
        if ($inSection) {
            if ($ct -match '^\S' -and $ct -ne '') { break }
            foreach ($m in [regex]::Matches($ct, $flagPattern)) { [void]$flags.Add($m.Groups[1].Value) }
        }
    }
    return $flags
}

foreach ($cmd in $commands | Sort-Object) {
    $cmdLines = (& twig ($cmd -split ' ') --help 2>&1 | Out-String) -split "`n"

    $optionFlags  = Get-SectionFlags $cmdLines 'Options'
    $exampleFlags = Get-SectionFlags $cmdLines 'Examples'

    foreach ($flag in $exampleFlags) {
        if (-not $optionFlags.Contains($flag)) {
            $mismatches += "MISMATCH: $cmd example uses '$flag' but Options section does not list it"
        }
    }
}

if ($mismatches.Count -gt 0) {
    Write-Host ""
    Write-Host "VALIDATION FAILED — $($mismatches.Count) mismatch(es) found:" -ForegroundColor Red
    foreach ($m in $mismatches) { Write-Host "  $m" -ForegroundColor Yellow }
    exit 1
}

Write-Host ""
Write-Host "VALIDATION PASSED — all example flags match their Options sections." -ForegroundColor Green
exit 0
