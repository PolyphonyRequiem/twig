<#
.SYNOPSIS
    Loads the ADO work tree for an Epic/Issue and outputs structured JSON
    for the pr_group_manager orchestrator.

.DESCRIPTION
    Deterministic replacement for the LLM-based work_tree_seeder in the
    implement workflow. Reads the ADO hierarchy via twig CLI and outputs
    the work_tree and pr_groups structure expected by downstream agents.

    Reads PR group assignments from the plan file if provided, otherwise
    creates a single PR group containing all tasks.

.PARAMETER WorkItemId
    The Epic or Issue ADO ID.

.PARAMETER PlanPath
    Optional path to the .plan.md file for PR group extraction.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [string]$PlanPath = ""
)

$ErrorActionPreference = 'Stop'

# Set context and get tree
$null = twig set $WorkItemId --output json 2>$null
$treeJson = twig tree --output json 2>$null
$tree = $treeJson | ConvertFrom-Json

if (-not $tree -or -not $tree.focus) {
    Write-Error "Failed to load work tree for #$WorkItemId"
    exit 1
}

$focus = $tree.focus
$children = $tree.children

# Build issue/task hierarchy
$issues = @()
$allTasks = @()
foreach ($child in $children) {
    if ($child.type -eq 'Issue') {
        # Get tasks under this issue
        $null = twig set $child.id --output json 2>$null
        $issueTree = twig tree --output json 2>$null | ConvertFrom-Json
        $tasks = @()
        if ($issueTree.children) {
            foreach ($t in $issueTree.children) {
                if ($t.type -eq 'Task') {
                    $tasks += [ordered]@{
                        id    = $t.id
                        title = $t.title
                        state = $t.state
                    }
                    $allTasks += $t
                }
            }
        }
        $issues += [ordered]@{
            id         = $child.id
            title      = $child.title
            state      = $child.state
            task_count = $tasks.Count
            tasks      = $tasks
        }
    }
    elseif ($child.type -eq 'Task') {
        # Direct child tasks (Issue-level input)
        $allTasks += $child
    }
}

# Try to extract PR groups from plan file
$prGroups = @()
if ($PlanPath -and (Test-Path $PlanPath)) {
    $planContent = Get-Content $PlanPath -Raw
    # Extract PR group sections: look for PG-N patterns
    $pgMatches = [regex]::Matches($planContent, '(?m)^#+\s*(PG-\d+)[:\s\u2014\-]+(.+?)$')
    $pgIndex = 0
    foreach ($m in $pgMatches) {
        $pgName = $m.Groups[1].Value
        $pgTitle = $m.Groups[2].Value.Trim()
        $pgIndex++

        # Try to find task/issue IDs associated with this PG
        $startPos = $m.Index + $m.Length
        $endPos = if ($pgIndex -lt $pgMatches.Count) { $pgMatches[$pgIndex].Index } else { $planContent.Length }
        $section = $planContent.Substring($startPos, [Math]::Min($endPos - $startPos, 2000))
        $idMatches = [regex]::Matches($section, '#(\d{4,})')
        $taskIds = @()
        $issueIds = @()
        foreach ($idm in $idMatches) {
            $refId = [int]$idm.Groups[1].Value
            if ($allTasks | Where-Object { $_.id -eq $refId }) {
                $taskIds += $refId
            }
            elseif ($issues | Where-Object { $_.id -eq $refId }) {
                $issueIds += $refId
            }
        }

        # Derive branch name
        $slug = ($pgTitle -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
        $branchName = "feature/$($pgName.ToLower())-$slug"
        if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }

        $prGroups += [ordered]@{
            name                   = $pgName
            title                  = $pgTitle
            task_ids               = $taskIds
            issue_ids              = $issueIds
            branch_name_suggestion = $branchName
        }
    }
}

# Fallback: single PR group with all tasks if none found in plan
if ($prGroups.Count -eq 0) {
    $slug = ($focus.title -replace '[^a-zA-Z0-9]+', '-').ToLower()
    if ($slug.Length -gt 50) { $slug = $slug.Substring(0, 50) }
    $prGroups += [ordered]@{
        name                   = "PG-1"
        title                  = $focus.title
        task_ids               = @($allTasks | ForEach-Object { $_.id })
        issue_ids              = @($issues | ForEach-Object { $_.id })
        branch_name_suggestion = "feature/$slug"
    }
}

# Output
$result = [ordered]@{
    work_tree    = [ordered]@{
        epic_id    = $focus.id
        epic_title = $focus.title
        epic_type  = $focus.type
        issues     = $issues
    }
    pr_groups    = $prGroups
    total_tasks  = $allTasks.Count
    total_issues = $issues.Count
}

$result | ConvertTo-Json -Depth 10 -Compress
