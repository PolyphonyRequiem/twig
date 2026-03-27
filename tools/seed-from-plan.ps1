#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seeds ADO work items from a plan document's EPIC headings.

.DESCRIPTION
    Parses a plan document for EPIC headings (supports both ### Epic N: and - EPIC-NNN: formats),
    creates an ADO Epic for the plan, Issues for each plan epic, and optionally Tasks for each
    task table row. Records the mapping in plan-ado-map.json for AB# commit linking.

    Hierarchy: ADO Epic (plan doc) → ADO Issue (plan epic) → ADO Task (task row, optional)
    Requires: twig CLI configured for dangreen-msft/Twig (or target org/project)

.EXAMPLE
    .\tools\seed-from-plan.ps1 -PlanFile "docs/projects/twig-interactive-nav.plan.md"
    .\tools\seed-from-plan.ps1 -PlanFile "docs/projects/twig-on-twig-integration.plan.md" -DryRun
    .\tools\seed-from-plan.ps1 -PlanFile "docs/projects/twig-interactive-nav.plan.md" -IncludeTasks
#>

param(
    [Parameter(Mandatory)][string]$PlanFile,
    [string]$PlanTitle,
    [string]$MapFile = "tools/plan-ado-map.json",
    [string]$AssignedTo = "Daniel Green",
    [hashtable]$EpicEffortHours = @{},
    [switch]$IncludeTasks,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Helpers ---

function Get-PlanTitle {
    param([string]$Path)
    $lines = Get-Content $Path -Encoding UTF8
    # Try YAML frontmatter 'goal:' field first
    $inFrontmatter = $false
    foreach ($line in $lines) {
        if ($line -match '^---\s*$') {
            if ($inFrontmatter) { break }
            $inFrontmatter = $true
            continue
        }
        if ($inFrontmatter -and $line -match '^\s*goal:\s*(.+)') {
            return $Matches[1].Trim()
        }
    }
    # Fall back to first H1 heading
    foreach ($line in $lines) {
        if ($line -match '^#\s+(.+)') {
            return $Matches[1].Trim()
        }
    }
    return [System.IO.Path]::GetFileNameWithoutExtension($Path)
}

function Get-PlanEpics {
    param([string]$Path)
    $lines = Get-Content $Path -Encoding UTF8
    $epics = @()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        # Pattern 1: ### Epic N: Title (twig-on-twig, field-enrichment, etc.)
        if ($line -match '^###\s+Epic\s+(\d+):\s*(.+)') {
            $num = [int]$Matches[1]
            $title = $Matches[2].Trim() -replace '\s*[—–-]+\s*(DONE|IN PROGRESS)\s*$', ''
            $epics += @{ Id = "Epic $num"; Title = $title; LineIndex = $i }
        }
        # Pattern 2: - EPIC-NNN: Title (twig-interactive-nav)
        elseif ($line -match '^-\s+EPIC-(\d+):\s*(.+)') {
            $num = $Matches[1]
            $title = $Matches[2].Trim() -replace '\s*[—–-]+\s*(DONE|IN PROGRESS)\s*$', ''
            $epics += @{ Id = "EPIC-$num"; Title = $title; LineIndex = $i }
        }
        # Pattern 3: ### EPIC-NNN: Title
        elseif ($line -match '^###\s+EPIC-(\d+):\s*(.+)') {
            $num = $Matches[1]
            $title = $Matches[2].Trim() -replace '\s*[—–-]+\s*(DONE|IN PROGRESS)\s*$', ''
            $epics += @{ Id = "EPIC-$num"; Title = $title; LineIndex = $i }
        }
    }
    return $epics
}

function Get-PlanDescription {
    param([string]$Path)
    $lines = Get-Content $Path -Encoding UTF8
    $collecting = $false
    $desc = @()
    foreach ($line in $lines) {
        # Start after '# Introduction' or first H1
        if (-not $collecting -and $line -match '^#\s+(Introduction|[^#])') {
            $collecting = $true
            continue
        }
        if ($collecting) {
            # Stop at next heading
            if ($line -match '^##\s') { break }
            $desc += $line
        }
    }
    $text = ($desc -join "`n").Trim()
    # Truncate to 4000 chars (ADO description limit)
    if ($text.Length -gt 4000) { $text = $text.Substring(0, 3997) + "..." }
    return $text
}

function Get-EpicDescription {
    param([string]$Path, [int]$EpicLineIndex, [int]$NextEpicLineIndex)
    $lines = Get-Content $Path -Encoding UTF8
    $endLine = if ($NextEpicLineIndex -gt 0) { $NextEpicLineIndex } else { $lines.Count }
    $desc = @()
    # Collect lines after the heading until a table row or next heading
    for ($i = $EpicLineIndex + 1; $i -lt $endLine; $i++) {
        $line = $lines[$i]
        # Stop at table header or next heading
        if ($line -match '^\|.*\b(Task|Description)\b.*\|' -or $line -match '^###?\s') { break }
        $desc += $line
    }
    $text = ($desc -join "`n").Trim()
    if ($text.Length -gt 4000) { $text = $text.Substring(0, 3997) + "..." }
    return $text
}

function Get-EpicTasks {
    param([string]$Path, [int]$EpicLineIndex, [int]$NextEpicLineIndex)
    $lines = Get-Content $Path -Encoding UTF8
    $tasks = @()
    $inTable = $false
    $headerSeen = $false

    $endLine = if ($NextEpicLineIndex -gt 0) { $NextEpicLineIndex } else { $lines.Count }

    for ($i = $EpicLineIndex; $i -lt $endLine; $i++) {
        $line = $lines[$i]
        # Detect table header row (must contain "Task" or "Description")
        if (-not $inTable -and $line -match '^\|.*\b(Task|Description)\b.*\|') {
            $inTable = $true
            $headerSeen = $false
            continue
        }
        # Skip separator row
        if ($inTable -and -not $headerSeen -and $line -match '^\|[\s-]+\|') {
            $headerSeen = $true
            continue
        }
        # Parse data rows
        if ($inTable -and $headerSeen) {
            if ($line -notmatch '^\|') {
                $inTable = $false
                continue
            }
            $cols = $line -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
            if ($cols.Count -ge 2) {
                $taskId = $cols[0]
                $desc = $cols[1]
                # Truncate description for ADO title (max 128 chars)
                if ($desc.Length -gt 120) {
                    $desc = $desc.Substring(0, 117) + "..."
                }
                $tasks += @{ Id = $taskId; Description = $desc }
            }
        }
    }
    return $tasks
}

function Invoke-TwigSeedNew {
    param([string]$Title, [string]$Type)
    $args = @("seed", "new", "--output", "json", $Title)
    if ($Type) { $args = @("seed", "new", "--type", $Type, "--output", "json", $Title) }
    $result = & twig @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "twig seed new failed: $result"
    }
    return $result
}

function Invoke-TwigSeedPublish {
    $result = & twig seed publish --all --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "twig seed publish failed: $result"
    }
    $json = $result | ConvertFrom-Json
    return $json
}

function Invoke-TwigSet {
    param([int]$Id)
    $result = & twig set $Id --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "twig set $Id failed: $result"
    }
}

function Has-Property {
    param($Obj, [string]$Name)
    if ($null -eq $Obj) { return $false }
    return [bool]($Obj.PSObject.Properties.Match($Name).Count -gt 0)
}

function Load-Map {
    param([string]$Path)
    if (Test-Path $Path) {
        return Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    return [pscustomobject]@{}
}

function Save-Map {
    param($Map, [string]$Path)
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $Map | ConvertTo-Json -Depth 10 | Set-Content $Path -Encoding UTF8
}

# --- Main ---

if (-not (Test-Path $PlanFile)) {
    Write-Error "Plan file not found: $PlanFile"
    exit 1
}

# Normalize to forward-slash relative path for map keys
$planKey = $PlanFile -replace '\\', '/'

# Resolve plan title
if (-not $PlanTitle) {
    $PlanTitle = Get-PlanTitle -Path $PlanFile
}

# Parse EPIC headings
$epics = Get-PlanEpics -Path $PlanFile
if ($epics.Count -eq 0) {
    Write-Warning "No EPIC headings found in $PlanFile"
    exit 0
}

Write-Host "`nPlan: $PlanTitle" -ForegroundColor Cyan
Write-Host "File: $planKey" -ForegroundColor DarkGray
Write-Host "Epics found: $($epics.Count)" -ForegroundColor DarkGray
Write-Host ""

# Load existing mapping
$map = Load-Map -Path $MapFile

# Check if plan already has an entry
$planEntry = $null
if (Has-Property $map $planKey) {
    $planEntry = $map.$planKey
    Write-Host "Existing mapping found for this plan (ADO Epic #$($planEntry.epicId))" -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host "[DRY RUN] Would create:" -ForegroundColor Magenta
    Write-Host "  ADO Epic: $PlanTitle" -ForegroundColor White
    foreach ($epic in $epics) {
        $existingIssue = $null
        if ($planEntry) {
            if (Has-Property $planEntry.epics $epic.Id) {
                $existingIssue = $planEntry.epics.($epic.Id).issueId
            }
        }
        if ($existingIssue) {
            Write-Host "  [SKIP] Issue: $($epic.Id): $($epic.Title) (already mapped to #$existingIssue)" -ForegroundColor DarkGray
        } else {
            Write-Host "  ADO Issue: $($epic.Id): $($epic.Title)" -ForegroundColor White
        }
        if ($IncludeTasks) {
            $nextIdx = -1
            $epicIdx = [Array]::IndexOf($epics, $epic)
            if ($epicIdx -lt $epics.Count - 1) { $nextIdx = $epics[$epicIdx + 1].LineIndex }
            $tasks = Get-EpicTasks -Path $PlanFile -EpicLineIndex $epic.LineIndex -NextEpicLineIndex $nextIdx
            foreach ($task in $tasks) {
                Write-Host "    ADO Task: $($task.Id): $($task.Description)" -ForegroundColor DarkGray
            }
        }
    }
    Write-Host "`n[DRY RUN] No changes made." -ForegroundColor Magenta
    exit 0
}

# --- Create ADO hierarchy ---

# Resolve the project's default area/iteration paths.
# Seeds inherit area/iteration from parent context, but top-level Epics (no parent)
# get empty paths, which ADO rejects. We patch these in SQLite before publish.
$twigConfig = Get-Content ".twig/config" -Raw | ConvertFrom-Json
$defaultAreaPath = $twigConfig.project  # Area path defaults to project name
$defaultIterationPath = $twigConfig.project
$dbPath = Join-Path ".twig" $twigConfig.organization $twigConfig.project "twig.db"

function Patch-SeedPaths {
    param([int]$SeedId)
    sqlite3 $dbPath "UPDATE work_items SET area_path = '$defaultAreaPath', iteration_path = '$defaultIterationPath' WHERE id = $SeedId AND (area_path IS NULL OR area_path = '');"
}

function Patch-SeedFields {
    param([int]$SeedId, [string]$Description, [string]$AssignedToValue, [double]$EffortHours = 0)
    # Patch assigned_to column
    if ($AssignedToValue) {
        $escaped = $AssignedToValue -replace "'", "''"
        sqlite3 $dbPath "UPDATE work_items SET assigned_to = '$escaped' WHERE id = $SeedId;"
    }
    # Build fields to merge into fields_json
    $fieldsToSet = @{}
    if ($Description) {
        $fieldsToSet['System.Description'] = $Description
    }
    if ($EffortHours -gt 0) {
        $fieldsToSet['Microsoft.VSTS.Scheduling.Effort'] = [string]$EffortHours
    }
    if ($fieldsToSet.Count -gt 0) {
        # Read existing fields_json, merge, write back
        $existingJson = sqlite3 $dbPath "SELECT fields_json FROM work_items WHERE id = $SeedId;"
        $existing = @{}
        if ($existingJson -and $existingJson -ne '{}') {
            try { $existing = $existingJson | ConvertFrom-Json -AsHashtable } catch { $existing = @{} }
        }
        foreach ($k in $fieldsToSet.Keys) { $existing[$k] = $fieldsToSet[$k] }
        $newJson = ($existing | ConvertTo-Json -Compress -Depth 5) -replace "'", "''"
        sqlite3 $dbPath "UPDATE work_items SET fields_json = '$newJson', is_dirty = 1 WHERE id = $SeedId;"
    }
}

function Clear-TwigContext {
    # Clear active work item so seed new --type Epic creates a parentless item
    sqlite3 $dbPath "DELETE FROM context WHERE key = 'active_work_item_id';"
}

# Extract plan-level description for the ADO Epic
$planDescription = Get-PlanDescription -Path $PlanFile

# Step 1: Create the ADO Epic for the plan (if not already mapped)
if (-not $planEntry) {
    Write-Host "Creating ADO Epic: $PlanTitle" -ForegroundColor Green
    # Clear context so seed is parentless (top-level Epic)
    Clear-TwigContext
    Invoke-TwigSeedNew -Title $PlanTitle -Type "Epic"
    # Patch area path before publish (top-level Epic has no parent to inherit from)
    $seedId = (sqlite3 $dbPath "SELECT MIN(id) FROM work_items WHERE is_seed = 1;")
    Patch-SeedPaths -SeedId $seedId
    # Patch description, assigned, effort on the seed before publish
    Patch-SeedFields -SeedId $seedId -Description $planDescription -AssignedToValue $AssignedTo
    $publishResult = Invoke-TwigSeedPublish
    $epicAdoId = $publishResult.results[0].newId
    Write-Host "  → ADO Epic #$epicAdoId" -ForegroundColor Green

    $planEntry = [pscustomobject]@{
        epicId = $epicAdoId
        epics  = [pscustomobject]@{}
    }
    $map | Add-Member -NotePropertyName $planKey -NotePropertyValue $planEntry -Force
    Save-Map -Map $map -Path $MapFile
} else {
    $epicAdoId = $planEntry.epicId
    Write-Host "Using existing ADO Epic #$epicAdoId" -ForegroundColor Yellow
}

# Step 2: Set the Epic as active parent context
Invoke-TwigSet -Id $epicAdoId

# Step 3: Create Issues for each plan epic
foreach ($epic in $epics) {
    $epicId = $epic.Id
    $epicTitle = "$epicId`: $($epic.Title)"

    # Idempotency: skip if already mapped
    if ($planEntry.epics -and (Has-Property $planEntry.epics $epicId)) {
        $existingId = $planEntry.epics.$epicId.issueId
        Write-Host "  [SKIP] $epicTitle (already mapped to #$existingId)" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Creating ADO Issue: $epicTitle" -ForegroundColor Green
    # Set parent to the plan Epic so Issue is created as child
    Invoke-TwigSet -Id $epicAdoId
    Invoke-TwigSeedNew -Title $epicTitle -Type ""
    # Extract epic description and effort, patch before publish
    $issueSeedId = (sqlite3 $dbPath "SELECT MIN(id) FROM work_items WHERE is_seed = 1;")
    $epicIdx = [Array]::IndexOf($epics, $epic)
    $nextIdx = -1
    if ($epicIdx -lt $epics.Count - 1) { $nextIdx = $epics[$epicIdx + 1].LineIndex }
    $epicDesc = Get-EpicDescription -Path $PlanFile -EpicLineIndex $epic.LineIndex -NextEpicLineIndex $nextIdx
    $effortVal = 0
    if ($EpicEffortHours.ContainsKey($epicId)) { $effortVal = $EpicEffortHours[$epicId] }
    Patch-SeedFields -SeedId $issueSeedId -Description $epicDesc -AssignedToValue $AssignedTo -EffortHours $effortVal
    $publishResult = Invoke-TwigSeedPublish
    $issueAdoId = $publishResult.results[0].newId
    Write-Host "    → ADO Issue #$issueAdoId" -ForegroundColor Green

    # Record mapping
    $planEntry.epics | Add-Member -NotePropertyName $epicId -NotePropertyValue ([pscustomobject]@{
        issueId = $issueAdoId
    }) -Force
    Save-Map -Map $map -Path $MapFile

    # Step 4: Optionally create Tasks for each task row
    if ($IncludeTasks) {
        $epicIdx = [Array]::IndexOf($epics, $epic)
        $nextIdx = -1
        if ($epicIdx -lt $epics.Count - 1) { $nextIdx = $epics[$epicIdx + 1].LineIndex }
        $tasks = Get-EpicTasks -Path $PlanFile -EpicLineIndex $epic.LineIndex -NextEpicLineIndex $nextIdx

        if ($tasks.Count -gt 0) {
            Invoke-TwigSet -Id $issueAdoId
            foreach ($task in $tasks) {
                $taskTitle = "$($task.Id): $($task.Description)"
                Write-Host "      Creating ADO Task: $taskTitle" -ForegroundColor DarkCyan
                Invoke-TwigSeedNew -Title $taskTitle -Type ""
                $taskPublish = Invoke-TwigSeedPublish
                $taskAdoId = $taskPublish.results[0].newId
                Write-Host "        → ADO Task #$taskAdoId" -ForegroundColor DarkCyan
            }
        }
    }
}

# Step 5: Transition plan Epic to "Doing" (implementation starting)
Write-Host "`nTransitioning ADO Epic #$epicAdoId to Doing..." -ForegroundColor Green
try {
    Invoke-TwigSet -Id $epicAdoId
    & twig state Doing --output json 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  → ADO Epic #$epicAdoId state: Doing" -ForegroundColor Green
    } else {
        Write-Host "  [WARN] Could not transition Epic to Doing (may already be in progress)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  [WARN] State transition failed: $_" -ForegroundColor Yellow
}

Write-Host "`nSeeding complete for: $PlanTitle" -ForegroundColor Cyan
Write-Host "Mapping saved to: $MapFile" -ForegroundColor DarkGray
