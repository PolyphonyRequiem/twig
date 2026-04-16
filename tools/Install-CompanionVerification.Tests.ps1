#!/usr/bin/env pwsh
# Pester tests for install.ps1 companion binary verification logic
# Run: Invoke-Pester .\tools\Install-CompanionVerification.Tests.ps1 -Output Detailed

Describe "install.ps1 companion verification" {
    BeforeAll {
        $script:RunVerification = {
            param([string]$installDir)
            $companions = @("twig-mcp.exe", "twig-tui.exe")
            foreach ($companion in $companions) {
                $companionPath = Join-Path $installDir $companion
                if (Test-Path $companionPath) {
                    Write-Output "Found $companion"
                } else {
                    Write-Warning "$companion not found in archive. Some features may be unavailable. Run 'twig upgrade' after install to fetch companions."
                }
            }
        }
    }

    BeforeEach {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "twig-install-test-$(New-Guid)"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }

    AfterEach {
        Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It "reports all companions found when present" {
        @("twig.exe", "twig-mcp.exe", "twig-tui.exe") | ForEach-Object {
            New-Item -ItemType File -Path (Join-Path $testDir $_) -Force | Out-Null
        }

        $output = & $script:RunVerification $testDir 3>&1

        $output | Should -Contain "Found twig-mcp.exe"
        $output | Should -Contain "Found twig-tui.exe"
        ($output | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }) | Should -BeNullOrEmpty
    }

    It "warns when <missing> is absent" -TestCases @(
        @{ present = @("twig.exe", "twig-tui.exe"); missing = "twig-mcp.exe" }
        @{ present = @("twig.exe", "twig-mcp.exe"); missing = "twig-tui.exe" }
    ) {
        $present | ForEach-Object { New-Item -ItemType File -Path (Join-Path $testDir $_) -Force | Out-Null }

        $warnings = (& $script:RunVerification $testDir 3>&1) | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

        $warnings | Should -HaveCount 1
        $warnings[0].Message | Should -BeLike "$missing*"
    }

    It "warns for both companions when archive has only twig.exe" {
        New-Item -ItemType File -Path (Join-Path $testDir "twig.exe") -Force | Out-Null

        $warnings = (& $script:RunVerification $testDir 3>&1) | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

        $warnings | Should -HaveCount 2
        $warnings[0].Message | Should -BeLike "twig-mcp.exe*"
        $warnings[1].Message | Should -BeLike "twig-tui.exe*"
    }
}
