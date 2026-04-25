#!/usr/bin/env pwsh
# ============================================================================
# Switch-TwigContext.Tests.ps1
# Pester tests for tools/Switch-TwigContext.ps1
# Run: Invoke-Pester .\tools\Switch-TwigContext.Tests.ps1 -Output Detailed
# ============================================================================

BeforeAll {
    $scriptPath = Join-Path $PSScriptRoot "Switch-TwigContext.ps1"
}

Describe "Switch-TwigContext" {
    BeforeEach {
        # Create an isolated workspace with config and two databases
        $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "twig-test-$(New-Guid)"
        New-Item -ItemType Directory -Path (Join-Path $testRoot ".twig" "orgA" "projA") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $testRoot ".twig" "orgB" "projB") -Force | Out-Null

        # Create stub databases
        [System.IO.File]::WriteAllBytes((Join-Path $testRoot ".twig" "orgA" "projA" "twig.db"), [byte[]]@(0))
        [System.IO.File]::WriteAllBytes((Join-Path $testRoot ".twig" "orgB" "projB" "twig.db"), [byte[]]@(0))

        $configPath = Join-Path $testRoot ".twig" "config"
        $initialConfig = @{
            organization    = "orgA"
            project         = "projA"
            team            = "teamA"
            auth            = @{ method = "azcli" }
            display         = @{ hints = $true; treeDepth = 10 }
            typeAppearances = @(
                @{ name = "Bug"; color = "CC293D"; iconId = "icon_insect" }
            )
        }
        [System.IO.File]::WriteAllText(
            $configPath,
            ($initialConfig | ConvertTo-Json -Depth 20),
            [System.Text.UTF8Encoding]::new($false)
        )
    }

    AfterEach {
        if (Test-Path $testRoot) {
            Remove-Item $testRoot -Recurse -Force
        }
        # Clean up any leftover backup files
        if ($env:TWIG_CONFIG_BACKUP -and (Test-Path $env:TWIG_CONFIG_BACKUP)) {
            Remove-Item $env:TWIG_CONFIG_BACKUP -Force
            $backupDir = Split-Path $env:TWIG_CONFIG_BACKUP -Parent
            if ((Test-Path $backupDir) -and @(Get-ChildItem $backupDir).Count -eq 0) {
                Remove-Item $backupDir -Force
            }
        }
        $env:TWIG_CONFIG_BACKUP = $null
    }

    Context "Switch-TwigContext function" {
        It "patches org, project, team fields" {
            . $scriptPath
            Switch-TwigContext -Org "orgB" -Project "projB" -Team "teamB" -Root $testRoot | Out-Null

            $result = Get-Content (Join-Path $testRoot ".twig" "config") -Raw | ConvertFrom-Json
            $result.organization | Should -Be "orgB"
            $result.project      | Should -Be "projB"
            $result.team         | Should -Be "teamB"
        }

        It "preserves all non-identity config settings" {
            . $scriptPath
            Switch-TwigContext -Org "orgB" -Project "projB" -Root $testRoot | Out-Null

            $result = Get-Content (Join-Path $testRoot ".twig" "config") -Raw | ConvertFrom-Json
            $result.auth.method             | Should -Be "azcli"
            $result.display.hints           | Should -Be $true
            $result.display.treeDepth       | Should -Be 10
            $result.typeAppearances[0].name | Should -Be "Bug"
        }

        It "defaults team to empty string when not specified" {
            . $scriptPath
            Switch-TwigContext -Org "orgB" -Project "projB" -Root $testRoot | Out-Null

            $result = Get-Content (Join-Path $testRoot ".twig" "config") -Raw | ConvertFrom-Json
            $result.team | Should -Be ""
        }

        It "fails when config does not exist" {
            . $scriptPath
            $emptyRoot = Join-Path ([System.IO.Path]::GetTempPath()) "twig-empty-$(New-Guid)"
            New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null

            $result = Switch-TwigContext -Org "orgA" -Project "projA" -Root $emptyRoot 2>$null
            $result | Should -Be $false

            Remove-Item $emptyRoot -Recurse -Force
        }

        It "fails when target database does not exist" {
            . $scriptPath
            $result = Switch-TwigContext -Org "orgC" -Project "projC" -Root $testRoot 2>$null
            $result | Should -Be $false

            # Config should remain unchanged
            $config = Get-Content (Join-Path $testRoot ".twig" "config") -Raw | ConvertFrom-Json
            $config.organization | Should -Be "orgA"
        }

        It "writes valid UTF-8 without BOM" {
            . $scriptPath
            Switch-TwigContext -Org "orgB" -Project "projB" -Root $testRoot | Out-Null

            $configPath = Join-Path $testRoot ".twig" "config"
            $bytes = [System.IO.File]::ReadAllBytes($configPath)
            # UTF-8 BOM is EF BB BF — should NOT be present
            if ($bytes.Length -ge 3) {
                ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -Be $false
            }
        }

        It "is a true no-op when values already match — file mtime is unchanged" {
            . $scriptPath
            $configPath = Join-Path $testRoot ".twig" "config"
            $before = Get-Content $configPath -Raw | ConvertFrom-Json
            $mtimeBefore = (Get-Item $configPath).LastWriteTimeUtc

            Start-Sleep -Milliseconds 150
            Switch-TwigContext -Org "orgA" -Project "projA" -Team "teamA" -Root $testRoot | Out-Null

            $after = Get-Content $configPath -Raw | ConvertFrom-Json
            $mtimeAfter = (Get-Item $configPath).LastWriteTimeUtc

            $after.organization | Should -Be $before.organization
            $after.project      | Should -Be $before.project
            $after.team         | Should -Be $before.team
            $mtimeAfter         | Should -Be $mtimeBefore
        }

        It "completes in under 2 seconds" {
            . $scriptPath
            $elapsed = Measure-Command {
                Switch-TwigContext -Org "orgB" -Project "projB" -Root $testRoot | Out-Null
            }
            $elapsed.TotalSeconds | Should -BeLessThan 2
        }

        It "rejects Org containing path traversal characters" {
            . $scriptPath
            $result = Switch-TwigContext -Org "..\evil" -Project "projA" -Root $testRoot 2>$null
            $result | Should -Be $false
        }

        It "rejects Project containing path separators" {
            . $scriptPath
            $result = Switch-TwigContext -Org "orgA" -Project "proj/evil" -Root $testRoot 2>$null
            $result | Should -Be $false
        }

        It "no-op works when config has null team and no -Team is passed" {
            . $scriptPath
            # Write a config with no team field
            $configPath = Join-Path $testRoot ".twig" "config"
            $noTeamConfig = @{
                organization = "orgA"
                project      = "projA"
                auth         = @{ method = "azcli" }
            }
            [System.IO.File]::WriteAllText(
                $configPath,
                ($noTeamConfig | ConvertTo-Json -Depth 20),
                [System.Text.UTF8Encoding]::new($false)
            )
            $mtimeBefore = (Get-Item $configPath).LastWriteTimeUtc

            Start-Sleep -Milliseconds 150
            Switch-TwigContext -Org "orgA" -Project "projA" -Root $testRoot | Out-Null

            $mtimeAfter = (Get-Item $configPath).LastWriteTimeUtc
            $mtimeAfter | Should -Be $mtimeBefore
        }
    }

    Context "Save-TwigConfig and Restore-TwigConfig" {
        It "round-trips config through save and restore" {
            . $scriptPath
            $configPath = Join-Path $testRoot ".twig" "config"
            $originalContent = Get-Content $configPath -Raw

            Save-TwigConfig -Root $testRoot | Out-Null
            Switch-TwigContext -Org "orgB" -Project "projB" -Team "teamB" -Root $testRoot | Out-Null

            # Config should now be orgB
            $switched = Get-Content $configPath -Raw | ConvertFrom-Json
            $switched.organization | Should -Be "orgB"

            Restore-TwigConfig -Root $testRoot | Out-Null

            # Config should be back to orgA
            $restored = Get-Content $configPath -Raw | ConvertFrom-Json
            $restored.organization | Should -Be "orgA"
            $restored.project      | Should -Be "projA"
            $restored.team         | Should -Be "teamA"
        }

        It "Save-TwigConfig sets TWIG_CONFIG_BACKUP env var" {
            . $scriptPath
            Save-TwigConfig -Root $testRoot | Out-Null
            $env:TWIG_CONFIG_BACKUP | Should -Not -BeNullOrEmpty
            Test-Path $env:TWIG_CONFIG_BACKUP | Should -Be $true
        }

        It "Restore-TwigConfig cleans up backup file" {
            . $scriptPath
            Save-TwigConfig -Root $testRoot | Out-Null
            $backupPath = $env:TWIG_CONFIG_BACKUP

            Restore-TwigConfig -Root $testRoot | Out-Null

            Test-Path $backupPath | Should -Be $false
            $env:TWIG_CONFIG_BACKUP | Should -BeNullOrEmpty
        }

        It "Restore-TwigConfig fails when no backup exists" {
            . $scriptPath
            $env:TWIG_CONFIG_BACKUP = $null
            $result = Restore-TwigConfig -Root $testRoot 2>$null
            $result | Should -Be $false
        }

        It "Save-TwigConfig fails when config does not exist" {
            . $scriptPath
            $emptyRoot = Join-Path ([System.IO.Path]::GetTempPath()) "twig-empty-$(New-Guid)"
            New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null

            $result = Save-TwigConfig -Root $emptyRoot 2>$null
            $result | Should -Be $false

            Remove-Item $emptyRoot -Recurse -Force
        }

        It "Save-TwigConfig fails on nested save without intervening restore" {
            . $scriptPath
            Save-TwigConfig -Root $testRoot | Out-Null
            $result = Save-TwigConfig -Root $testRoot 2>$null
            $result | Should -Be $false

            # Original backup should still exist (not overwritten)
            Test-Path $env:TWIG_CONFIG_BACKUP | Should -Be $true
        }

        It "preserves full config integrity through save/switch/restore cycle" {
            . $scriptPath
            $configPath = Join-Path $testRoot ".twig" "config"

            # Read original config fully
            $original = Get-Content $configPath -Raw | ConvertFrom-Json

            # Save → Switch → Restore
            Save-TwigConfig -Root $testRoot | Out-Null
            Switch-TwigContext -Org "orgB" -Project "projB" -Team "teamB" -Root $testRoot | Out-Null
            Restore-TwigConfig -Root $testRoot | Out-Null

            $restored = Get-Content $configPath -Raw | ConvertFrom-Json

            # Every field should match
            $restored.organization              | Should -Be $original.organization
            $restored.project                   | Should -Be $original.project
            $restored.team                      | Should -Be $original.team
            $restored.auth.method               | Should -Be $original.auth.method
            $restored.display.hints             | Should -Be $original.display.hints
            $restored.display.treeDepth         | Should -Be $original.display.treeDepth
            $restored.typeAppearances[0].name   | Should -Be $original.typeAppearances[0].name
            $restored.typeAppearances[0].color  | Should -Be $original.typeAppearances[0].color
            $restored.typeAppearances[0].iconId | Should -Be $original.typeAppearances[0].iconId
        }
    }

    Context "Script invocation (non-dot-sourced)" {
        It "switches context when invoked with parameters" {
            & $scriptPath -Org "orgB" -Project "projB" -Team "teamB" -WorkspaceRoot $testRoot

            $result = Get-Content (Join-Path $testRoot ".twig" "config") -Raw | ConvertFrom-Json
            $result.organization | Should -Be "orgB"
            $result.project      | Should -Be "projB"
            $result.team         | Should -Be "teamB"
        }

        It "does nothing when invoked without parameters (dot-source import)" {
            $configPath = Join-Path $testRoot ".twig" "config"
            $before = Get-Content $configPath -Raw

            . $scriptPath

            $after = Get-Content $configPath -Raw
            $after | Should -Be $before
        }

        It "does not leak ErrorActionPreference into caller scope when dot-sourced" {
            $ErrorActionPreference = 'Continue'
            . $scriptPath
            $ErrorActionPreference | Should -Be 'Continue'
        }
    }
}
