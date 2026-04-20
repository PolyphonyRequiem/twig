# check-duplicate-session.ps1
# Preflight duplicate-session detector for twig-sdlc.
# Emits a single-line JSON object on stdout with:
#   is_duplicate (bool), details (string), reason (string)
#
# Signals used:
#   1. %TEMP%/conductor/*.events.jsonl with matching "work_item_id" and
#      recent mtime (within -StaleMinutes) AND > 10 event lines (to exclude
#      the event file of the calling run itself, which is brand new).
#   2. `git worktree list --porcelain` — another worktree has sdlc/<id>
#      checked out and it's not the current working directory.
#
# Exits 0 on success regardless of result. Non-zero only on script error.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)][int]$WorkItemId = 0,
    [int]$StaleMinutes = 10,
    [int]$MinEventLines = 10
)

$ErrorActionPreference = 'Stop'

function Write-Result {
    param([bool]$IsDuplicate, [string]$Details, [string]$Reason)
    $o = [ordered]@{
        is_duplicate = $IsDuplicate
        details      = $Details
        reason       = $Reason
    }
    ConvertTo-Json -InputObject $o -Depth 3 -Compress
}

if ($WorkItemId -le 0) {
    Write-Result $false '' 'No work_item_id provided (prompt-mode or new-epic run) — skipping duplicate check.'
    exit 0
}

$tmp = [System.IO.Path]::GetTempPath()
$conductorDir = Join-Path $tmp 'conductor'

$eventHits = @()
if (Test-Path $conductorDir) {
    $threshold = (Get-Date).AddMinutes(-$StaleMinutes)
    $pattern = '"(work_item_id|epic_id)"\s*:\s*' + [regex]::Escape("$WorkItemId") + '\b'

    # Our own conductor run advertises its events file via CONDUCTOR_EVENTS_FILE
    # (set by conductor's EventLogSubscriber). Exclude it so we don't self-detect.
    $ownEventsFile = $env:CONDUCTOR_EVENTS_FILE
    $ownEventsName = if ($ownEventsFile) { Split-Path -Leaf $ownEventsFile } else { '' }

    Get-ChildItem $conductorDir -Filter '*.events.jsonl' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $threshold } |
        Where-Object { -not $ownEventsName -or $_.Name -ne $ownEventsName } |
        ForEach-Object {
            $file = $_
            try {
                # scan whole file — epic_id lands in intake's agent_completed event,
                # which may be deep into the log; head-only scans miss it
                $lines = Get-Content -Path $file.FullName -ErrorAction Stop
            } catch {
                return
            }
            if ($lines.Count -lt $MinEventLines) { return }
            $match = $false
            foreach ($line in $lines) {
                if ($line -match $pattern) { $match = $true; break }
            }
            if ($match) {
                $ageSec = [int]((Get-Date) - $file.LastWriteTime).TotalSeconds
                $eventHits += [pscustomobject]@{
                    File           = $file.Name
                    LastActivitySec = $ageSec
                    Lines          = $lines.Count
                }
            }
        }
}

$worktreeHits = @()
try {
    $wtOutput = git worktree list --porcelain 2>$null
    if ($LASTEXITCODE -eq 0 -and $wtOutput) {
        # Use git to find OUR worktree root — robust regardless of cwd.
        # Falls back to Get-Location if not inside a repo for some reason.
        $selfRoot = (git rev-parse --show-toplevel 2>$null)
        if (-not $selfRoot -or $LASTEXITCODE -ne 0) {
            $selfRoot = (Get-Location).Path
        }
        $selfRoot = $selfRoot.Replace('/', '\').TrimEnd('\', '/')
        $branchPattern = "refs/heads/sdlc/$WorkItemId"
        $blocks = ($wtOutput -join "`n") -split "`n`n"
        foreach ($block in $blocks) {
            $pathMatch = [regex]::Match($block, '(?m)^worktree\s+(.+)$')
            $branchMatch = [regex]::Match($block, "(?m)^branch\s+$([regex]::Escape($branchPattern))\b")
            if ($pathMatch.Success -and $branchMatch.Success) {
                $wtPath = $pathMatch.Groups[1].Value.Trim().Replace('/', '\').TrimEnd('\', '/')
                if ($wtPath -and $wtPath -ne $selfRoot) {
                    $worktreeHits += $wtPath
                }
            }
        }
    }
} catch {
    # git may not be available or not in a repo — non-fatal
}

if ($eventHits.Count -gt 0) {
    $summary = ($eventHits | ForEach-Object {
        "$($_.File) (last activity $($_.LastActivitySec)s ago, $($_.Lines)+ events)"
    }) -join '; '
    Write-Result $true $summary "Found $($eventHits.Count) active conductor event log(s) for work item #$WorkItemId within the last $StaleMinutes min."
    exit 0
}

if ($worktreeHits.Count -gt 0) {
    $summary = "Branch sdlc/$WorkItemId is checked out in other worktree(s): " + ($worktreeHits -join ', ')
    Write-Result $true $summary 'A sibling worktree already owns this work item branch.'
    exit 0
}

Write-Result $false '' "No active runs or branch conflicts found for work item #$WorkItemId."
exit 0
