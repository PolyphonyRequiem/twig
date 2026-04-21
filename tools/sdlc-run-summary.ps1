<#
.SYNOPSIS
    Analyzes a conductor workflow event log and produces a run summary.

.DESCRIPTION
    Reads a conductor .events.jsonl file and outputs structured metrics:
    duration, token usage, cost, agent execution counts, loop detection,
    route frequency, approval rates, and anomaly flags.

.PARAMETER EventLog
    Path to the conductor .events.jsonl file.

.PARAMETER Output
    Output format: "human" (default) or "json".

.EXAMPLE
    .\sdlc-run-summary.ps1 -EventLog "$env:TEMP\conductor\conductor-twig-sdlc-*.events.jsonl"
    .\sdlc-run-summary.ps1 -EventLog "path/to/events.jsonl" -Output json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EventLog,
    [ValidateSet('human', 'json')][string]$Output = 'human'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EventLog)) {
    Write-Error "Event log not found: $EventLog"
    exit 1
}

# Parse all events
$events = Get-Content $EventLog | ForEach-Object {
    $_ | ConvertFrom-Json -ErrorAction SilentlyContinue
} | Where-Object { $_ -ne $null }

if ($events.Count -eq 0) {
    Write-Error "No events found in $EventLog"
    exit 1
}

# ── Basic run info ──────────────────────────────────────────────────
$started = $events | Where-Object { $_.type -eq 'workflow_started' } | Select-Object -First 1
$completed = $events | Where-Object { $_.type -eq 'workflow_completed' } | Select-Object -First 1
$failed = $events | Where-Object { $_.type -eq 'workflow_failed' } | Select-Object -Last 1

$workflowName = if ($started) { $started.data.name } else { 'unknown' }
$status = if ($completed) { 'completed' } elseif ($failed) { 'failed' } else { 'unknown' }
$startTime = if ($started) { $started.timestamp } else { 0 }
$endTime = if ($completed) { $completed.timestamp } elseif ($failed) { $failed.timestamp } else { ($events[-1]).timestamp }
$durationSec = [math]::Round($endTime - $startTime, 1)
$durationMin = [math]::Round($durationSec / 60, 1)

# ── Agent execution counts ─────────────────────────────────────────
$agentCompletions = $events | Where-Object { $_.type -eq 'agent_completed' }
$agentCounts = @{}
$agentTokens = @{}
$agentCosts = @{}
$agentDurations = @{}

foreach ($e in $agentCompletions) {
    $name = $e.data.agent_name
    if (-not $agentCounts.ContainsKey($name)) {
        $agentCounts[$name] = 0
        $agentTokens[$name] = 0
        $agentCosts[$name] = 0.0
        $agentDurations[$name] = 0.0
    }
    $agentCounts[$name]++
    $agentTokens[$name] += [int]($e.data.tokens ?? 0)
    $agentCosts[$name] += [double]($e.data.cost_usd ?? 0)
    $agentDurations[$name] += [double]($e.data.elapsed ?? 0)
}

$totalTokens = ($agentTokens.Values | Measure-Object -Sum).Sum
$totalCost = [math]::Round(($agentCosts.Values | Measure-Object -Sum).Sum, 4)
$totalAgentRuns = ($agentCounts.Values | Measure-Object -Sum).Sum

# ── Route frequency ────────────────────────────────────────────────
$routes = $events | Where-Object { $_.type -eq 'route_taken' }
$routeCounts = @{}
foreach ($r in $routes) {
    $key = "$($r.data.from_agent)->$($r.data.to_agent)"
    if (-not $routeCounts.ContainsKey($key)) { $routeCounts[$key] = 0 }
    $routeCounts[$key]++
}

# ── Loop detection ─────────────────────────────────────────────────
$loops = @{}
foreach ($key in $routeCounts.Keys) {
    $parts = $key -split '->'
    if ($parts.Count -eq 2) {
        $reverse = "$($parts[1])->$($parts[0])"
        if ($routeCounts.ContainsKey($reverse)) {
            $loopName = ($parts | Sort-Object) -join ' <-> '
            if (-not $loops.ContainsKey($loopName)) {
                $loops[$loopName] = [math]::Min($routeCounts[$key], $routeCounts[$reverse])
            }
        }
    }
}

# ── Approval rates ─────────────────────────────────────────────────
$taskReviews = $agentCompletions | Where-Object { $_.data.agent_name -eq 'task_reviewer' }
$taskApproved = ($taskReviews | Where-Object { $_.data.output.approved -eq $true }).Count
$taskTotal = $taskReviews.Count

$issueReviews = $agentCompletions | Where-Object { $_.data.agent_name -eq 'issue_reviewer' }
$issueApproved = ($issueReviews | Where-Object { $_.data.output.approved -eq $true }).Count
$issueTotal = $issueReviews.Count

$prReviews = $agentCompletions | Where-Object { $_.data.agent_name -eq 'pr_reviewer' }
$prApproved = ($prReviews | Where-Object { $_.data.output.approved -eq $true }).Count
$prTotal = $prReviews.Count

# ── Review scores ──────────────────────────────────────────────────
$techReview = $agentCompletions | Where-Object { $_.data.agent_name -eq 'technical_reviewer' } | Select-Object -Last 1
$readReview = $agentCompletions | Where-Object { $_.data.agent_name -eq 'readability_reviewer' } | Select-Object -Last 1
$techScore = if ($techReview) { $techReview.data.output.score } else { $null }
$readScore = if ($readReview) { $readReview.data.output.score } else { $null }

# ── Anomaly detection ──────────────────────────────────────────────
$anomalies = @()
foreach ($name in $agentCounts.Keys) {
    if ($agentCounts[$name] -gt 5) {
        $anomalies += "Agent '$name' ran $($agentCounts[$name]) times (potential loop)"
    }
}
if ($totalCost -gt 50) {
    $anomalies += "Total cost `$$totalCost exceeds `$50 threshold"
}
if ($durationSec -gt 14400) {
    $anomalies += "Duration $durationMin min exceeds 4-hour threshold"
}
$costLeader = $agentCosts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
if ($costLeader -and $totalCost -gt 0 -and ($costLeader.Value / $totalCost) -gt 0.3) {
    $pct = [math]::Round(($costLeader.Value / $totalCost) * 100, 0)
    $anomalies += "Agent '$($costLeader.Key)' is $pct% of total cost"
}

# ── Work item ID extraction ───────────────────────────────────────
$workItemId = 0
$intakeCompleted = $agentCompletions | Where-Object { $_.data.agent_name -eq 'intake' } | Select-Object -First 1
if ($intakeCompleted -and $intakeCompleted.data.output.epic_id) {
    $workItemId = $intakeCompleted.data.output.epic_id
}

# ── Build result ───────────────────────────────────────────────────
$result = [ordered]@{
    run_id         = [System.IO.Path]::GetFileNameWithoutExtension($EventLog)
    workflow_name  = $workflowName
    work_item_id   = $workItemId
    status         = $status
    duration_sec   = $durationSec
    duration_min   = $durationMin
    total_tokens   = $totalTokens
    total_cost_usd = $totalCost
    total_agent_runs = $totalAgentRuns
    agent_counts   = $agentCounts
    agent_costs    = $agentCosts | ForEach-Object { $h = @{}; $_.GetEnumerator() | ForEach-Object { $h[$_.Key] = [math]::Round($_.Value, 4) }; $h }
    loops          = $loops
    approval_rates = [ordered]@{
        task_reviewer  = if ($taskTotal -gt 0) { "$taskApproved/$taskTotal" } else { 'n/a' }
        issue_reviewer = if ($issueTotal -gt 0) { "$issueApproved/$issueTotal" } else { 'n/a' }
        pr_reviewer    = if ($prTotal -gt 0) { "$prApproved/$prTotal" } else { 'n/a' }
    }
    review_scores  = [ordered]@{
        technical    = $techScore
        readability  = $readScore
    }
    anomalies      = $anomalies
    error          = if ($failed) { $failed.data.message } else { $null }
}

# ── Output ─────────────────────────────────────────────────────────
if ($Output -eq 'json') {
    $result | ConvertTo-Json -Depth 5 -Compress
}
else {
    Write-Host "`n═══ SDLC Run Summary ═══" -ForegroundColor Cyan
    Write-Host "Workflow:    $workflowName"
    Write-Host "Work Item:   #$workItemId"
    Write-Host "Status:      $status"
    Write-Host "Duration:    $durationMin min ($durationSec sec)"
    Write-Host "Tokens:      $($totalTokens.ToString('N0'))"
    Write-Host "Cost:        `$$totalCost"
    Write-Host "Agent Runs:  $totalAgentRuns"

    if ($techScore -or $readScore) {
        Write-Host "`n── Review Scores ──" -ForegroundColor Yellow
        if ($techScore) { Write-Host "  Technical:    $techScore/100" }
        if ($readScore) { Write-Host "  Readability:  $readScore/100" }
    }

    Write-Host "`n── Agent Breakdown ──" -ForegroundColor Yellow
    $agentCounts.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
        $cost = if ($agentCosts[$_.Key]) { "`$$([math]::Round($agentCosts[$_.Key], 2))" } else { '' }
        $dur = if ($agentDurations[$_.Key]) { "$([math]::Round($agentDurations[$_.Key] / 60, 1))m" } else { '' }
        Write-Host ("  {0,-25} {1,3}x  {2,8}  {3,6}" -f $_.Key, $_.Value, $cost, $dur)
    }

    Write-Host "`n── Approval Rates ──" -ForegroundColor Yellow
    Write-Host "  Task Reviewer:   $taskApproved/$taskTotal"
    Write-Host "  Issue Reviewer:  $issueApproved/$issueTotal"
    Write-Host "  PR Reviewer:     $prApproved/$prTotal"

    if ($loops.Count -gt 0) {
        Write-Host "`n── Loops Detected ──" -ForegroundColor Yellow
        $loops.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
            Write-Host "  $($_.Key): $($_.Value) cycles"
        }
    }

    if ($anomalies.Count -gt 0) {
        Write-Host "`n── Anomalies ──" -ForegroundColor Red
        foreach ($a in $anomalies) { Write-Host "  ⚠ $a" }
    }

    if ($failed) {
        Write-Host "`n── Error ──" -ForegroundColor Red
        Write-Host "  $($failed.data.error_type): $($failed.data.message.Substring(0, [Math]::Min(200, $failed.data.message.Length)))"
    }

    Write-Host ""
}
