#!/usr/bin/env pwsh
# ============================================================================
# seed-from-plan.Tests.ps1
# Pester tests for Get-PlanTitle and Get-PlanWorkItemId in seed-from-plan.ps1
# Run: Invoke-Pester .\tools\seed-from-plan.Tests.ps1 -Output Detailed
# ============================================================================

Describe "seed-from-plan helper functions" {
    BeforeAll {
        # Extract the helper functions from the script by defining them here
        # (mirrors the implementations in seed-from-plan.ps1)
        function Get-PlanTitle {
            param([string]$Path)
            $lines = Get-Content $Path -Encoding UTF8
            $inFrontmatter = $false
            $fmTitle = $null
            $fmGoal = $null
            foreach ($line in $lines) {
                if ($line -match '^---\s*$') {
                    if ($inFrontmatter) { break }
                    $inFrontmatter = $true
                    continue
                }
                if ($inFrontmatter -and $line -match '^\s*title:\s*"?([^"]+)"?\s*$') {
                    $fmTitle = $Matches[1].Trim()
                }
                if ($inFrontmatter -and $line -match '^\s*goal:\s*(.+)') {
                    $fmGoal = $Matches[1].Trim()
                }
            }
            if ($fmTitle) { return $fmTitle }
            if ($fmGoal) { return $fmGoal }
            foreach ($line in $lines) {
                if ($line -match '^#\s+(.+)') {
                    return $Matches[1].Trim()
                }
            }
            return [System.IO.Path]::GetFileNameWithoutExtension($Path)
        }

        function Get-PlanWorkItemId {
            param([string]$Path)
            $lines = Get-Content $Path -Encoding UTF8
            $inFrontmatter = $false
            foreach ($line in $lines) {
                if ($line -match '^---\s*$') {
                    if ($inFrontmatter) { break }
                    $inFrontmatter = $true
                    continue
                }
                if ($inFrontmatter -and $line -match '^\s*work_item_id:\s*(\d+)') {
                    return [int]$Matches[1]
                }
            }
            return $null
        }
    }

    BeforeEach {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "seed-plan-test-$(New-Guid)"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }

    AfterEach {
        Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context "Get-PlanTitle" {
        It "returns frontmatter title when title and goal are both present" {
            $file = Join-Path $testDir "plan.md"
            @"
---
work_item_id: 1918
title: "My Plan Title"
goal: My Goal
type: Issue
---
# H1 Heading
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "My Plan Title"
        }

        It "returns frontmatter title without quotes" {
            $file = Join-Path $testDir "plan.md"
            @"
---
title: Unquoted Title
---
# H1 Heading
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "Unquoted Title"
        }

        It "falls back to goal when title is absent" {
            $file = Join-Path $testDir "plan.md"
            @"
---
goal: My Goal Value
---
# H1 Heading
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "My Goal Value"
        }

        It "falls back to H1 heading when no frontmatter" {
            $file = Join-Path $testDir "plan.md"
            @"
# My H1 Heading
Some content here
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "My H1 Heading"
        }

        It "falls back to H1 when frontmatter has neither title nor goal" {
            $file = Join-Path $testDir "plan.md"
            @"
---
work_item_id: 100
type: Issue
---
# Fallback H1
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "Fallback H1"
        }

        It "falls back to filename when no frontmatter and no H1" {
            $file = Join-Path $testDir "my-plan-name.md"
            @"
Some content without headings
More content
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "my-plan-name"
        }

        It "prefers title over goal regardless of field order" {
            $file = Join-Path $testDir "plan.md"
            @"
---
goal: Goal First
title: "Title Second"
---
# H1
"@ | Set-Content $file -Encoding UTF8
            Get-PlanTitle -Path $file | Should -Be "Title Second"
        }
    }

    Context "Get-PlanWorkItemId" {
        It "returns work_item_id from frontmatter" {
            $file = Join-Path $testDir "plan.md"
            @"
---
work_item_id: 1918
title: "My Plan"
type: Issue
---
# Heading
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -Be 1918
            Get-PlanWorkItemId -Path $file | Should -BeOfType [int]
        }

        It "returns null when no frontmatter exists" {
            $file = Join-Path $testDir "plan.md"
            @"
# Heading
Some content
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -BeNullOrEmpty
        }

        It "returns null when frontmatter has no work_item_id" {
            $file = Join-Path $testDir "plan.md"
            @"
---
title: "My Plan"
type: Issue
---
# Heading
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -BeNullOrEmpty
        }

        It "handles work_item_id with extra whitespace" {
            $file = Join-Path $testDir "plan.md"
            @"
---
work_item_id:   42
---
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -Be 42
        }

        It "does not match work_item_id outside frontmatter" {
            $file = Join-Path $testDir "plan.md"
            @"
# Heading
work_item_id: 999
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -BeNullOrEmpty
        }

        It "does not match work_item_id after frontmatter closes" {
            $file = Join-Path $testDir "plan.md"
            @"
---
title: "Plan"
---
work_item_id: 555
"@ | Set-Content $file -Encoding UTF8
            Get-PlanWorkItemId -Path $file | Should -BeNullOrEmpty
        }
    }
}
