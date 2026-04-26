#!/usr/bin/env pwsh
# ============================================================================
# preflight-lite.Tests.ps1
# Pester v5 tests for recursive/scripts/preflight-lite.ps1
# Run: Invoke-Pester .\tools\preflight-lite.Tests.ps1 -Output Detailed
# ============================================================================

BeforeAll {
    $Script:ScriptPath = Join-Path $env:USERPROFILE ".conductor" "registries" "twig" "recursive" "scripts" "preflight-lite.ps1"
    if (-not (Test-Path $Script:ScriptPath)) {
        throw "preflight-lite.ps1 not found at $Script:ScriptPath"
    }
}

Describe "preflight-lite.ps1" {
    BeforeAll {
    # Helper: invoke the lite script with all externals mocked and return parsed JSON
    function Invoke-PreflightLite {
        param(
            [int]$WorkItemId = 1234,
            [hashtable]$MockOverrides = @{}
        )

        # Defaults for all-pass scenario
        $defaults = @{
            GhApiUser  = 'testuser'
            GitDir     = '.git'
            GitBranch  = 'feature/test-branch'
            TwigSet    = "Active work item: #$WorkItemId - Test Item"
        }

        foreach ($key in $MockOverrides.Keys) {
            $defaults[$key] = $MockOverrides[$key]
        }
        $cfg = $defaults

        $wrapperScript = @"
`$ErrorActionPreference = 'Stop'

function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]`$Args_)
    `$joined = `$Args_ -join ' '
    if (`$joined -match 'api user') {
        if ('$($cfg.GhApiUser)' -eq '__THROW__') { throw 'gh auth failed' }
        if ('$($cfg.GhApiUser)' -eq '') { return `$null }
        return '$($cfg.GhApiUser)'
    }
    return ''
}

function git {
    param([Parameter(ValueFromRemainingArguments)][string[]]`$Args_)
    `$joined = `$Args_ -join ' '
    if (`$joined -match 'rev-parse --git-dir') {
        if ('$($cfg.GitDir)' -eq '__THROW__') { throw 'git not available' }
        if ('$($cfg.GitDir)' -eq '') { return `$null }
        return '$($cfg.GitDir)'
    }
    if (`$joined -match 'branch --show-current') {
        if ('$($cfg.GitBranch)' -eq '__THROW__') { throw 'git branch failed' }
        return '$($cfg.GitBranch)'
    }
    return ''
}

function twig {
    param([Parameter(ValueFromRemainingArguments)][string[]]`$Args_)
    `$joined = `$Args_ -join ' '
    if (`$joined -match '^set') {
        if ('$($cfg.TwigSet)' -eq '__THROW__') { throw 'twig set failed' }
        return '$($cfg.TwigSet)'
    }
    return ''
}

& '$($Script:ScriptPath -replace "'", "''")' -WorkItemId $WorkItemId
"@

        $tempScript = Join-Path ([System.IO.Path]::GetTempPath()) "preflight-lite-test-$(New-Guid).ps1"
        try {
            Set-Content -Path $tempScript -Value $wrapperScript -Encoding UTF8
            $rawOutput = & pwsh -NoProfile -NonInteractive -File $tempScript 2>&1
            $jsonText = ($rawOutput | Where-Object { $_ -is [string] -or $_.GetType().Name -ne 'ErrorRecord' }) -join "`n"
            return $jsonText | ConvertFrom-Json
        } finally {
            Remove-Item $tempScript -Force -ErrorAction SilentlyContinue
        }
    }
    } # end BeforeAll

    Context "All checks pass" {
        It "returns ready=true when all 3 checks pass" {
            $result = Invoke-PreflightLite
            $result.ready | Should -Be $true
        }

        It "returns has_warnings=false (lite has no advisory checks)" {
            $result = Invoke-PreflightLite
            $result.has_warnings | Should -Be $false
        }

        It "returns failed_count=0" {
            $result = Invoke-PreflightLite
            $result.failed_count | Should -Be 0
        }

        It "returns warning_count=0" {
            $result = Invoke-PreflightLite
            $result.warning_count | Should -Be 0
        }

        It "summary indicates all checks passed" {
            $result = Invoke-PreflightLite
            $result.summary | Should -BeLike "*All*passed*"
        }
    }

    Context "Output schema validation" {
        BeforeAll {
            $Script:LiteResult = Invoke-PreflightLite
        }

        It "has 'ready' boolean field" {
            $Script:LiteResult.ready | Should -BeOfType [bool]
        }

        It "has 'has_warnings' boolean field" {
            $Script:LiteResult.has_warnings | Should -BeOfType [bool]
        }

        It "has 'required_checks' array with 3 items" {
            $Script:LiteResult.required_checks | Should -Not -BeNullOrEmpty
            $Script:LiteResult.required_checks.Count | Should -Be 3
        }

        It "has 'advisory_checks' as empty array" {
            # When ConvertTo-Json serializes an empty @(), it may deserialize as $null
            if ($null -ne $Script:LiteResult.advisory_checks) {
                $Script:LiteResult.advisory_checks.Count | Should -Be 0
            }
            # If null, that's acceptable — no advisory checks in lite mode
        }

        It "has 'failed_count' numeric field" {
            $Script:LiteResult.failed_count | Should -BeOfType [long]
        }

        It "has 'warning_count' field equal to 0" {
            $Script:LiteResult.warning_count | Should -Be 0
        }

        It "has 'summary' string field" {
            $Script:LiteResult.summary | Should -Not -BeNullOrEmpty
        }

        It "each required check has name, passed, detail, category fields" {
            foreach ($check in $Script:LiteResult.required_checks) {
                $check.name     | Should -Not -BeNullOrEmpty
                $check.passed   | Should -Not -BeNullOrEmpty
                $check.detail   | Should -Not -BeNullOrEmpty
                $check.category | Should -Be 'required'
            }
        }
    }

    Context "Required check names" {
        It "contains exactly 3 checks: gh_auth, git_repo, ado_access" {
            $result = Invoke-PreflightLite
            $names = $result.required_checks | ForEach-Object { $_.name }
            $names | Should -Contain 'gh_auth'
            $names | Should -Contain 'git_repo'
            $names | Should -Contain 'ado_access'
            $names.Count | Should -Be 3
        }
    }

    Context "Individual check failures" {
        It "gh_auth fails when gh api user throws" {
            $result = Invoke-PreflightLite -MockOverrides @{ GhApiUser = '__THROW__' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'gh_auth' }
            $check.passed | Should -Be $false
            $check.remediation | Should -Not -BeNullOrEmpty
        }

        It "gh_auth fails when gh returns empty user" {
            $result = Invoke-PreflightLite -MockOverrides @{ GhApiUser = '' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'gh_auth' }
            $check.passed | Should -Be $false
        }

        It "git_repo fails when git rev-parse throws" {
            $result = Invoke-PreflightLite -MockOverrides @{ GitDir = '__THROW__' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'git_repo' }
            $check.passed | Should -Be $false
            $check.remediation | Should -Not -BeNullOrEmpty
        }

        It "git_repo fails when not in a git repository" {
            $result = Invoke-PreflightLite -MockOverrides @{ GitDir = '' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'git_repo' }
            $check.passed | Should -Be $false
        }

        It "ado_access fails when twig set throws" {
            $result = Invoke-PreflightLite -MockOverrides @{ TwigSet = '__THROW__' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'ado_access' }
            $check.passed | Should -Be $false
            $check.remediation | Should -Not -BeNullOrEmpty
        }

        It "ado_access fails when work item not found" {
            $result = Invoke-PreflightLite -MockOverrides @{ TwigSet = 'No work item found' }
            $result.ready | Should -Be $false
            $check = $result.required_checks | Where-Object { $_.name -eq 'ado_access' }
            $check.passed | Should -Be $false
        }
    }

    Context "Remediation strings on failure" {
        It "every failed check has a non-empty remediation" {
            $result = Invoke-PreflightLite -MockOverrides @{
                GhApiUser = '__THROW__'
                GitDir    = '__THROW__'
                TwigSet   = '__THROW__'
            }
            $failed = $result.required_checks | Where-Object { -not $_.passed }
            $failed.Count | Should -Be 3
            foreach ($check in $failed) {
                $check.remediation | Should -Not -BeNullOrEmpty -Because "check '$($check.name)' should have remediation"
            }
        }
    }

    Context "No advisory checks in lite mode" {
        It "advisory_checks array remains empty even when all required fail" {
            $result = Invoke-PreflightLite -MockOverrides @{
                GhApiUser = '__THROW__'
                GitDir    = '__THROW__'
                TwigSet   = '__THROW__'
            }
            $result.advisory_checks.Count | Should -Be 0
            $result.has_warnings | Should -Be $false
            $result.warning_count | Should -Be 0
        }
    }

    Context "Summary messages" {
        It "all pass summary mentions all checks passed" {
            $result = Invoke-PreflightLite
            $result.summary | Should -BeLike "*All*check*passed*"
        }

        It "failure summary lists failed check names" {
            $result = Invoke-PreflightLite -MockOverrides @{ GhApiUser = '__THROW__' }
            $result.summary | Should -BeLike "*gh_auth*"
        }

        It "multiple failures summary lists all failed names" {
            $result = Invoke-PreflightLite -MockOverrides @{
                GhApiUser = '__THROW__'
                GitDir    = '__THROW__'
            }
            $result.summary | Should -BeLike "*gh_auth*"
            $result.summary | Should -BeLike "*git_repo*"
        }
    }

    Context "Edge cases" {
        It "handles WorkItemId=0 gracefully" {
            $result = Invoke-PreflightLite -WorkItemId 0 -MockOverrides @{
                TwigSet = 'No work item found'
            }
            $result | Should -Not -BeNullOrEmpty
            $result.required_checks | Should -Not -BeNullOrEmpty
        }

        It "handles large WorkItemId" {
            $result = Invoke-PreflightLite -WorkItemId 999999
            $result | Should -Not -BeNullOrEmpty
            $result.ready | Should -Be $true
        }

        It "single check failure sets ready=false" {
            $result = Invoke-PreflightLite -MockOverrides @{ GitDir = '__THROW__' }
            $result.ready | Should -Be $false
            $result.failed_count | Should -BeGreaterOrEqual 1
        }

        It "all checks fail sets failed_count=3" {
            $result = Invoke-PreflightLite -MockOverrides @{
                GhApiUser = '__THROW__'
                GitDir    = '__THROW__'
                TwigSet   = '__THROW__'
            }
            $result.ready | Should -Be $false
            $result.failed_count | Should -Be 3
        }
    }

    Context "Performance" {
        It "completes in under 5 seconds" {
            $elapsed = Measure-Command {
                Invoke-PreflightLite | Out-Null
            }
            $elapsed.TotalSeconds | Should -BeLessThan 5
        }
    }
}
