<#
.SYNOPSIS
    Runs conductor implement workflow for a queue of (plan, epic) pairs sequentially.

.DESCRIPTION
    Accepts a list of plan/epic pairs from a queue file (tools/epic-queue.txt) or
    inline parameters. For each pair, runs the conductor implement workflow, waits
    for completion, auto-commits, and moves to the next.

    Queue file format (one per line):
        plan_path | EPIC-NNN
        plan_path | EPIC-NNN

    Lines starting with # are comments. Blank lines are skipped.
    Successfully completed entries are prefixed with "# DONE: " in-place.

.PARAMETER QueueFile
    Path to the queue file. Defaults to tools/epic-queue.txt

.PARAMETER DryRun
    Show what would be executed without running anything.

.PARAMETER NoCommit
    Run the conductor workflow but skip the auto-commit step.

.PARAMETER StopOnFailure
    Stop processing the queue if any epic fails. Default: true.

.EXAMPLE
    .\tools\run-epics.ps1
    .\tools\run-epics.ps1 -DryRun
    .\tools\run-epics.ps1 -QueueFile tools/my-queue.txt
#>
param(
    [string]$QueueFile = "tools/epic-queue.txt",
    [switch]$DryRun,
    [switch]$NoCommit,
    [switch]$StopOnFailure,
    [int]$MaxRetries = 3
)

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel 2>$null) ?? (Get-Location).Path
$workflowPath = Join-Path $repoRoot ".github/skills/octane-workflow-implement/assets/implement.yaml"
$queuePath = Join-Path $repoRoot $QueueFile

# ── Validation ────────────────────────────────────────────────────
if (-not (Test-Path $queuePath)) {
    Write-Host "Queue file not found: $queuePath" -ForegroundColor Red
    Write-Host "Create it with format:  plan_path | EPIC-NNN" -ForegroundColor Yellow
    exit 1
}

if (-not (Get-Command conductor -ErrorAction SilentlyContinue)) {
    Write-Host "conductor CLI not found in PATH" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $workflowPath)) {
    Write-Host "Workflow not found: $workflowPath" -ForegroundColor Red
    exit 1
}

# ── Parse queue ───────────────────────────────────────────────────
$lines = Get-Content $queuePath
$queue = @()
$lineNumbers = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i].Trim()
    if ($line -eq "" -or $line.StartsWith("#")) { continue }

    $parts = $line -split '\|', 2
    if ($parts.Count -ne 2) {
        Write-Host "Skipping malformed line $($i+1): $line" -ForegroundColor Yellow
        continue
    }

    $plan = $parts[0].Trim()
    $epic = $parts[1].Trim()

    if (-not (Test-Path (Join-Path $repoRoot $plan))) {
        Write-Host "Plan file not found: $plan (line $($i+1))" -ForegroundColor Red
        if ($StopOnFailure) { exit 1 }
        continue
    }

    $queue += [PSCustomObject]@{ Plan = $plan; Epic = $epic; LineIndex = $i }
    $lineNumbers += $i
}

if ($queue.Count -eq 0) {
    Write-Host "No entries in queue (all done or empty)" -ForegroundColor Green
    exit 0
}

# ── Summary ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  EPIC Queue Runner — $($queue.Count) epic(s) to implement" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

foreach ($entry in $queue) {
    Write-Host "  $($entry.Epic) from $($entry.Plan)" -ForegroundColor White
}
Write-Host ""

if ($DryRun) {
    Write-Host "[DRY RUN] Would execute the above. Exiting." -ForegroundColor Yellow
    exit 0
}

# ── Execute queue ─────────────────────────────────────────────────
$completed = 0
$failed = 0
$startTime = Get-Date

foreach ($entry in $queue) {
    $epicStart = Get-Date
    $epicNum = $completed + $failed + 1
    Write-Host ""
    Write-Host "───────────────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host "[$epicNum/$($queue.Count)] $($entry.Epic) from $($entry.Plan)" -ForegroundColor Cyan
    Write-Host "───────────────────────────────────────────────────" -ForegroundColor DarkGray

    # Run conductor
    Push-Location $repoRoot
    try {
        $conductorArgs = @(
            "run", $workflowPath,
            "--input", "plan=`"$($entry.Plan)`"",
            "--input", "epic=`"$($entry.Epic)`""
        )

        $epicPassed = $false
        for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
            if ($attempt -gt 1) {
                Write-Host "  Retry $attempt/$MaxRetries — rolling back and re-running conductor..." -ForegroundColor Yellow
                git checkout -- . 2>$null
                git clean -fd 2>$null
            }

            Write-Host "  Running: conductor $($conductorArgs -join ' ')" -ForegroundColor DarkGray
            & conductor @conductorArgs 2>&1 | ForEach-Object {
                Write-Host "  $_"
            }
            $exitCode = $LASTEXITCODE

            if ($exitCode -ne 0) {
                Write-Host "  Conductor FAILED (exit code $exitCode)" -ForegroundColor Red
                if ($attempt -lt $MaxRetries) {
                    Write-Host "  Will retry..." -ForegroundColor Yellow
                    continue
                }
                break
            }

            # Run tests
            Write-Host "  Running tests (attempt $attempt/$MaxRetries)..." -ForegroundColor DarkGray
            $testOutput = dotnet test --nologo --verbosity quiet 2>&1
            $testExit = $LASTEXITCODE
            $testSummary = ($testOutput | Select-Object -Last 3) -join "`n"

            if ($testExit -eq 0) {
                Write-Host "  Tests passed" -ForegroundColor Green
                $epicPassed = $true
                break
            }

            Write-Host "  Tests FAILED (attempt $attempt/$MaxRetries)" -ForegroundColor Red
            Write-Host $testSummary -ForegroundColor Red
        }

        if (-not $epicPassed) {
            Write-Host "  EPIC FAILED after $MaxRetries attempts" -ForegroundColor Red
            git checkout -- . 2>$null
            git clean -fd 2>$null
            Write-Host "  Working tree rolled back to last good state" -ForegroundColor Yellow
            $lines[$entry.LineIndex] = "# FAILED: $($lines[$entry.LineIndex])"
            Set-Content -Path $queuePath -Value $lines
            $failed++
            if ($StopOnFailure) {
                Write-Host "  Stopping queue (StopOnFailure=true)" -ForegroundColor Red
                break
            }
            continue
        }

        # Auto-commit
        if (-not $NoCommit) {
            $planName = [System.IO.Path]::GetFileNameWithoutExtension($entry.Plan) -replace '\.plan$', ''
            $commitMsg = "feat($planName): implement $($entry.Epic)"

            git add -A 2>$null
            $hasChanges = git diff --cached --quiet 2>$null; $hasChanges = $LASTEXITCODE -ne 0

            if ($hasChanges) {
                git commit -m $commitMsg 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                Write-Host "  Committed: $commitMsg" -ForegroundColor Green
            } else {
                Write-Host "  No changes to commit" -ForegroundColor Yellow
            }
        }

        # Mark as done in queue file
        $lines[$entry.LineIndex] = "# DONE: $($lines[$entry.LineIndex])"
        Set-Content -Path $queuePath -Value $lines

        $elapsed = (Get-Date) - $epicStart
        Write-Host "  Completed in $([math]::Round($elapsed.TotalMinutes, 1)) min" -ForegroundColor Green
        $completed++

    } finally {
        Pop-Location
    }
}

# ── Summary ───────────────────────────────────────────────────────
$totalElapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Queue complete: $completed succeeded, $failed failed" -ForegroundColor $(if ($failed -gt 0) { "Yellow" } else { "Green" })
Write-Host "  Total time: $([math]::Round($totalElapsed.TotalMinutes, 1)) min" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
