#!/usr/bin/env pwsh
# ============================================================================
# preflight-check.Tests.ps1
# Pester v5 tests for recursive/scripts/preflight-check.ps1
# Run: Invoke-Pester .\tools\preflight-check.Tests.ps1 -Output Detailed
# ============================================================================

BeforeAll {
    $Script:TargetScript = Join-Path $env:USERPROFILE ".conductor" "registries" "twig" "recursive" "scripts" "preflight-check.ps1"
    $Script:WrapperScript = Join-Path $PSScriptRoot "preflight-check-wrapper.ps1"
    if (-not (Test-Path $Script:TargetScript)) {
        throw "preflight-check.ps1 not found at $Script:TargetScript"
    }
    if (-not (Test-Path $Script:WrapperScript)) {
        throw "preflight-check-wrapper.ps1 not found at $Script:WrapperScript"
    }
}

Describe "preflight-check.ps1" {
    BeforeAll {
        function Invoke-PreflightCheck {
            param(
                [int]$WorkItemId = 1234,
                [hashtable]$MockOverrides = @{}
            )

            $defaults = @{
                GhApiUser        = 'testuser'
                GitRemoteUrl     = 'https://github.com/TestOrg/TestRepo.git'
                GhApiRepoPerms   = 'true'
                TwigSync         = ''
                TwigSet          = "Active work item: #$WorkItemId - Test Item"
                TwigStateHelp    = 'twig state [options]'
                DotnetVersion    = '10.0.100'
                GitBranch        = 'feature/test-branch'
                GhRepoSetDefault = ''
                ConductorVersion = 'conductor 1.2.3'
                TwigMcpPath      = 'C:\Users\test\.twig\bin\twig-mcp.exe'
                TwigConfigExists = 'true'
                AdoNetworkOk     = 'true'
                GhNetworkOk      = 'true'
            }

            foreach ($key in $MockOverrides.Keys) {
                $defaults[$key] = $MockOverrides[$key]
            }

            $configFile = Join-Path ([System.IO.Path]::GetTempPath()) "preflight-test-cfg-$(New-Guid).json"
            try {
                $defaults | ConvertTo-Json | Set-Content -Path $configFile -Encoding UTF8
                $rawOutput = & pwsh -NoProfile -NonInteractive -File $Script:WrapperScript `
                    -ConfigPath $configFile `
                    -ScriptPath $Script:TargetScript `
                    -WorkItemId $WorkItemId 2>&1
                $jsonText = ($rawOutput | Out-String).Trim()
                return $jsonText | ConvertFrom-Json
            } finally {
                Remove-Item $configFile -Force -ErrorAction SilentlyContinue
            }
        }
    } # end BeforeAll

    Context "All checks pass" {
        It "returns ready=true when all required and advisory checks pass" {
            $result = Invoke-PreflightCheck
            $result.ready | Should -Be $true
        }

        It "returns has_warnings=false when all advisory checks pass" {
            $result = Invoke-PreflightCheck
            $result.has_warnings | Should -Be $false
        }

        It "returns failed_count=0" {
            $result = Invoke-PreflightCheck
            $result.failed_count | Should -Be 0
        }

        It "returns warning_count=0" {
            $result = Invoke-PreflightCheck
            $result.warning_count | Should -Be 0
        }

        It "summary indicates all checks passed" {
            $result = Invoke-PreflightCheck
            $result.summary | Should -BeLike "*All*check*passed*"
        }
    }

    Context "Output schema validation" {
        BeforeAll {
            $Script:SchemaResult = Invoke-PreflightCheck
        }

        It "has 'ready' boolean field" {
            $Script:SchemaResult.ready | Should -BeOfType [bool]
        }

        It "has 'has_warnings' boolean field" {
            $Script:SchemaResult.has_warnings | Should -BeOfType [bool]
        }

        It "has 'required_checks' array with 7 items" {
            $Script:SchemaResult.required_checks | Should -Not -BeNullOrEmpty
            $Script:SchemaResult.required_checks.Count | Should -Be 7
        }

        It "has 'advisory_checks' array with 5 items" {
            $Script:SchemaResult.advisory_checks | Should -Not -BeNullOrEmpty
            $Script:SchemaResult.advisory_checks.Count | Should -Be 5
        }

        It "has 'checks' combined array with 12 items" {
            $Script:SchemaResult.checks | Should -Not -BeNullOrEmpty
            $Script:SchemaResult.checks.Count | Should -Be 12
        }

        It "has 'failed_count' numeric field" {
            $Script:SchemaResult.failed_count | Should -BeOfType [long]
        }

        It "has 'warning_count' numeric field" {
            $Script:SchemaResult.warning_count | Should -BeOfType [long]
        }

        It "has 'summary' string field" {
            $Script:SchemaResult.summary | Should -Not -BeNullOrEmpty
        }

        It "each required check has name, passed, detail, category fields" {
            foreach ($check in $Script:SchemaResult.required_checks) {
                $check.name     | Should -Not -BeNullOrEmpty
                $check.passed   | Should -Not -BeNullOrEmpty
                $check.detail   | Should -Not -BeNullOrEmpty
                $check.category | Should -Be 'required'
            }
        }

        It "each advisory check has name, passed, detail, category fields" {
            foreach ($check in $Script:SchemaResult.advisory_checks) {
                $check.name     | Should -Not -BeNullOrEmpty
                $check.category | Should -Be 'advisory'
            }
        }
    }

    Context "Required check names" {
        BeforeAll {
            $Script:NamesResult = Invoke-PreflightCheck
        }

        It "contains all 7 required check names" {
            $expectedNames = @('gh_auth', 'gh_push', 'ado_access', 'twig_state', 'dotnet_sdk', 'git_status', 'gh_default_repo')
            $actualNames = $Script:NamesResult.required_checks | ForEach-Object { $_.name }
            foreach ($name in $expectedNames) {
                $actualNames | Should -Contain $name
            }
        }

        It "contains all 5 advisory check names" {
            $expectedNames = @('conductor_version', 'twig_mcp_binary', 'twig_config', 'network_ado', 'network_github')
            $actualNames = $Script:NamesResult.advisory_checks | ForEach-Object { $_.name }
            foreach ($name in $expectedNames) {
                $actualNames | Should -Contain $name
            }
        }
    }

    Context "Required/advisory separation semantics" {
        It "advisory failure does not set ready=false" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                ConductorVersion = '__THROW__'
                TwigMcpPath      = ''
                TwigConfigExists = 'false'
                AdoNetworkOk     = 'false'
                GhNetworkOk      = 'false'
            }
            $result.ready | Should -Be $true
        }

        It "advisory failure sets has_warnings=true" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                ConductorVersion = '__THROW__'
            }
            $result.has_warnings | Should -Be $true
        }

        It "advisory failure increments warning_count" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                TwigMcpPath      = ''
                TwigConfigExists = 'false'
            }
            $result.warning_count | Should -BeGreaterOrEqual 2
        }

        It "required failure sets ready=false" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                DotnetVersion = '__THROW__'
            }
            $result.ready | Should -Be $false
        }

        It "required failure increments failed_count" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                DotnetVersion = '__THROW__'
            }
            $result.failed_count | Should -BeGreaterOrEqual 1
        }
    }

    Context "Individual required check failures" {
        It "gh_auth fails when gh api user throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GhApiUser = '__THROW__' }
            $result.ready | Should -Be $false
            $ghAuth = $result.required_checks | Where-Object { $_.name -eq 'gh_auth' }
            $ghAuth.passed | Should -Be $false
            $ghAuth.remediation | Should -Not -BeNullOrEmpty
        }

        It "gh_push fails when push access is not true" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GhApiRepoPerms = 'false' }
            $result.ready | Should -Be $false
            $ghPush = $result.required_checks | Where-Object { $_.name -eq 'gh_push' }
            $ghPush.passed | Should -Be $false
            $ghPush.remediation | Should -Not -BeNullOrEmpty
        }

        It "gh_push fails when origin is not a GitHub URL" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GitRemoteUrl = 'https://gitlab.com/Org/Repo.git' }
            $result.ready | Should -Be $false
            $ghPush = $result.required_checks | Where-Object { $_.name -eq 'gh_push' }
            $ghPush.passed | Should -Be $false
        }

        It "ado_access fails when twig set throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ TwigSet = '__THROW__' }
            $result.ready | Should -Be $false
            $ado = $result.required_checks | Where-Object { $_.name -eq 'ado_access' }
            $ado.passed | Should -Be $false
            $ado.remediation | Should -Not -BeNullOrEmpty
        }

        It "ado_access fails when work item not found" {
            $result = Invoke-PreflightCheck -MockOverrides @{ TwigSet = 'No work item found' }
            $result.ready | Should -Be $false
            $ado = $result.required_checks | Where-Object { $_.name -eq 'ado_access' }
            $ado.passed | Should -Be $false
        }

        It "twig_state fails when twig state help throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ TwigStateHelp = '__THROW__' }
            $result.ready | Should -Be $false
            $ts = $result.required_checks | Where-Object { $_.name -eq 'twig_state' }
            $ts.passed | Should -Be $false
            $ts.remediation | Should -Not -BeNullOrEmpty
        }

        It "dotnet_sdk fails when dotnet throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ DotnetVersion = '__THROW__' }
            $result.ready | Should -Be $false
            $dn = $result.required_checks | Where-Object { $_.name -eq 'dotnet_sdk' }
            $dn.passed | Should -Be $false
            $dn.remediation | Should -Not -BeNullOrEmpty
        }

        It "git_status fails when git branch throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GitBranch = '__THROW__' }
            $result.ready | Should -Be $false
            $gs = $result.required_checks | Where-Object { $_.name -eq 'git_status' }
            $gs.passed | Should -Be $false
        }

        It "gh_default_repo fails when gh repo set-default throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GhRepoSetDefault = '__THROW__' }
            $result.ready | Should -Be $false
            $gd = $result.required_checks | Where-Object { $_.name -eq 'gh_default_repo' }
            $gd.passed | Should -Be $false
            $gd.remediation | Should -Not -BeNullOrEmpty
        }
    }

    Context "Individual advisory check failures" {
        It "conductor_version warns when conductor throws" {
            $result = Invoke-PreflightCheck -MockOverrides @{ ConductorVersion = '__THROW__' }
            $result.ready | Should -Be $true
            $cv = $result.advisory_checks | Where-Object { $_.name -eq 'conductor_version' }
            $cv.passed | Should -Be $false
            $cv.remediation | Should -Not -BeNullOrEmpty
        }

        It "twig_mcp_binary warns when binary not found" {
            $result = Invoke-PreflightCheck -MockOverrides @{ TwigMcpPath = '' }
            $result.ready | Should -Be $true
            $tm = $result.advisory_checks | Where-Object { $_.name -eq 'twig_mcp_binary' }
            $tm.passed | Should -Be $false
            $tm.remediation | Should -Not -BeNullOrEmpty
        }

        It "twig_config warns when .twig/ directory missing" {
            $result = Invoke-PreflightCheck -MockOverrides @{ TwigConfigExists = 'false' }
            $result.ready | Should -Be $true
            $tc = $result.advisory_checks | Where-Object { $_.name -eq 'twig_config' }
            $tc.passed | Should -Be $false
            $tc.remediation | Should -Not -BeNullOrEmpty
        }

        It "network_ado warns when dev.azure.com unreachable" {
            $result = Invoke-PreflightCheck -MockOverrides @{ AdoNetworkOk = 'false' }
            $result.ready | Should -Be $true
            $na = $result.advisory_checks | Where-Object { $_.name -eq 'network_ado' }
            $na.passed | Should -Be $false
            $na.remediation | Should -Not -BeNullOrEmpty
        }

        It "network_github warns when github.com unreachable" {
            $result = Invoke-PreflightCheck -MockOverrides @{ GhNetworkOk = 'false' }
            $result.ready | Should -Be $true
            $ng = $result.advisory_checks | Where-Object { $_.name -eq 'network_github' }
            $ng.passed | Should -Be $false
            $ng.remediation | Should -Not -BeNullOrEmpty
        }
    }

    Context "Remediation strings" {
        It "most failed required checks have a non-empty remediation" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                GhApiUser        = '__THROW__'
                GhApiRepoPerms   = '__THROW__'
                TwigSet          = '__THROW__'
                TwigStateHelp    = '__THROW__'
                DotnetVersion    = '__THROW__'
                GitBranch        = '__THROW__'
                GhRepoSetDefault = '__THROW__'
            }
            $failed = $result.required_checks | Where-Object { -not $_.passed }
            $failed.Count | Should -Be 7
            # Most checks provide remediation; git_status omits it in the original script
            $withRemediation = $failed | Where-Object { $_.remediation }
            $withRemediation.Count | Should -BeGreaterOrEqual 5
        }
    }

    Context "Mixed failure scenarios" {
        It "required pass + advisory fail = ready=true, has_warnings=true" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                ConductorVersion = '__THROW__'
                TwigMcpPath      = ''
                TwigConfigExists = 'false'
                AdoNetworkOk     = 'false'
                GhNetworkOk      = 'false'
            }
            $result.ready | Should -Be $true
            $result.has_warnings | Should -Be $true
            $result.warning_count | Should -Be 5
            $result.failed_count | Should -Be 0
        }

        It "required fail + advisory pass = ready=false, has_warnings=false" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                DotnetVersion = '__THROW__'
            }
            $result.ready | Should -Be $false
            $result.has_warnings | Should -Be $false
            $result.failed_count | Should -BeGreaterOrEqual 1
        }

        It "both required and advisory fail" {
            $result = Invoke-PreflightCheck -MockOverrides @{
                DotnetVersion    = '__THROW__'
                ConductorVersion = '__THROW__'
            }
            $result.ready | Should -Be $false
            $result.has_warnings | Should -Be $true
            $result.failed_count | Should -BeGreaterOrEqual 1
            $result.warning_count | Should -BeGreaterOrEqual 1
        }
    }

    Context "Summary messages" {
        It "all pass summary mentions all checks passed" {
            $result = Invoke-PreflightCheck
            $result.summary | Should -BeLike "*All*check*passed*"
        }

        It "advisory-only failure summary mentions warnings" {
            $result = Invoke-PreflightCheck -MockOverrides @{ ConductorVersion = '__THROW__' }
            $result.summary | Should -BeLike "*advisory*warning*"
        }

        It "required failure summary lists failed check names" {
            $result = Invoke-PreflightCheck -MockOverrides @{ DotnetVersion = '__THROW__' }
            $result.summary | Should -BeLike "*dotnet_sdk*"
        }
    }

    Context "Edge cases" {
        It "handles WorkItemId=0 gracefully" {
            $result = Invoke-PreflightCheck -WorkItemId 0 -MockOverrides @{
                TwigSet = 'No work item found'
            }
            $result | Should -Not -BeNullOrEmpty
            $result.required_checks | Should -Not -BeNullOrEmpty
        }

        It "checks array is the union of required and advisory" {
            $result = Invoke-PreflightCheck
            $result.checks.Count | Should -Be ($result.required_checks.Count + $result.advisory_checks.Count)
        }
    }

    Context "Performance" {
        It "completes in under 5 seconds" {
            $elapsed = Measure-Command {
                Invoke-PreflightCheck | Out-Null
            }
            $elapsed.TotalSeconds | Should -BeLessThan 5
        }
    }
}
