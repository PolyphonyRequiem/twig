#!/usr/bin/env pwsh
# ============================================================================
# Committer-AdoIntegration.Tests.ps1
# Pester tests validating the conductor committer prompt includes ADO
# work-item linking (AB#) and state-transition instructions.
# Run: Invoke-Pester .\tools\Committer-AdoIntegration.Tests.ps1 -Output Detailed
# ============================================================================

BeforeAll {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $implementYaml = Join-Path $repoRoot ".github" "skills" "octane-workflow-implement" "assets" "implement.yaml"
    $mapFile = Join-Path $repoRoot "tools" "plan-ado-map.json"
}

Describe "Committer ADO Integration — Prompt Content (E2-T1..T4)" {

    Context "implement.yaml contains AB# linking instructions" {

        BeforeAll {
            $yamlContent = Get-Content $implementYaml -Raw
        }

        It "references tools/plan-ado-map.json for ADO mapping lookup" {
            $yamlContent | Should -Match 'tools/plan-ado-map\.json'
        }

        It "instructs the committer to append AB#<issueId> to commit messages" {
            $yamlContent | Should -Match 'AB#<issueId>'
        }

        It "shows a concrete AB# commit message example" {
            $yamlContent | Should -Match 'AB#\d+'
        }

        It "includes the plan-ado-map.json structure documentation" {
            $yamlContent | Should -Match 'epicId'
            $yamlContent | Should -Match 'issueId'
        }

        It "handles the missing mapping file case — commit normally without AB#" {
            $yamlContent | Should -Match '(?i)missing.*commit normally|commit normally.*without AB#'
        }
    }

    Context "implement.yaml contains twig state transition instructions" {

        BeforeAll {
            $yamlContent = Get-Content $implementYaml -Raw
        }

        It "instructs running twig set <issueId> to set active work item" {
            $yamlContent | Should -Match 'twig set <issueId> --output json'
        }

        It "instructs running twig state Done to transition the item" {
            $yamlContent | Should -Match 'twig state Done --output json'
        }

        It "includes advisory error handling — failures must not block execution" {
            $yamlContent | Should -Match '(?i)warning.*continue|advisory|never block'
        }
    }
}

Describe "Committer ADO Integration — Mapping File Validation" {

    Context "plan-ado-map.json exists and has valid structure" {

        It "plan-ado-map.json file exists" {
            Test-Path $mapFile | Should -Be $true
        }

        It "is valid JSON" {
            { Get-Content $mapFile -Raw | ConvertFrom-Json } | Should -Not -Throw
        }

        It "has at least one plan entry keyed by .plan.md path" {
            $map = Get-Content $mapFile -Raw | ConvertFrom-Json
            $planKeys = $map.PSObject.Properties.Name
            $planKeys.Count | Should -BeGreaterThan 0
            $planKeys | ForEach-Object { $_ | Should -Match '\.plan\.md$' }
        }

        It "each plan entry has a numeric epicId" {
            $map = Get-Content $mapFile -Raw | ConvertFrom-Json
            foreach ($prop in $map.PSObject.Properties) {
                $prop.Value.epicId | Should -BeOfType [long]
            }
        }

        It "each plan entry has an epics object with numeric issueId values" {
            $map = Get-Content $mapFile -Raw | ConvertFrom-Json
            foreach ($planProp in $map.PSObject.Properties) {
                $epics = $planProp.Value.epics
                $epics | Should -Not -BeNullOrEmpty
                foreach ($epicProp in $epics.PSObject.Properties) {
                    $epicProp.Value.issueId | Should -BeOfType [long]
                }
            }
        }
    }

    Context "No-mapping-file scenario (E2-T6 regression)" {

        It "prompt instructs to commit normally when mapping is absent" {
            $yamlContent = Get-Content $implementYaml -Raw
            # The prompt must handle the case where the file doesn't exist
            $yamlContent | Should -Match 'file is missing'
        }

        It "prompt never makes AB# mandatory" {
            $yamlContent = Get-Content $implementYaml -Raw
            # Ensure there's no language making AB# required
            $yamlContent | Should -Not -Match '(?i)must include AB#|AB# is required|error.*no AB#'
        }
    }
}

Describe "Committer ADO Integration — Prompt Ordering" {

    BeforeAll {
        $yamlContent = Get-Content $implementYaml -Raw
    }

    It "AB# linking appears before twig state transition in the prompt" {
        $abIndex = $yamlContent.IndexOf('AB#<issueId>')
        $twigStateIndex = $yamlContent.IndexOf('twig state Done')
        $abIndex | Should -BeLessThan $twigStateIndex
    }

    It "twig set appears before twig state Done" {
        $setIndex = $yamlContent.IndexOf('twig set <issueId>')
        $stateIndex = $yamlContent.IndexOf('twig state Done')
        $setIndex | Should -BeLessThan $stateIndex
    }

    It "error handling guidance appears after twig commands" {
        $stateIndex = $yamlContent.IndexOf('twig state Done')
        $advisoryIndex = $yamlContent.IndexOf('advisory')
        $advisoryIndex | Should -BeGreaterThan $stateIndex
    }
}
