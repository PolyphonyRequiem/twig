#!/usr/bin/env pwsh
# ============================================================================
# Validate-CommandExamples.Tests.ps1
# Pester tests for tools/Validate-CommandExamples.ps1 parsing logic.
# Run: Invoke-Pester .\tools\Validate-CommandExamples.Tests.ps1 -Output Detailed
# ============================================================================

BeforeAll {
    $script:categoryPattern  = '^\w[\w\s]*:$'
    $script:commandPattern   = '^\s{2}(\S+(?:\s\S+)*)\s{2,}'
    $script:placeholderToken = '<[^>]+>'
    $script:optionalArgToken = '\[[^\]]+\]'
    $script:flagVariantToken = '--[a-z][a-z0-9-]*'
    $script:flagPattern      = '(--[a-z][a-z0-9-]*)'

    function script:Get-SectionFlags([string[]]$lines, [string]$section) {
        $flags = [System.Collections.Generic.HashSet[string]]::new()
        $inSection = $false
        foreach ($cl in $lines) {
            $ct = $cl.TrimEnd()
            if ($ct -match "^${section}:") { $inSection = $true; continue }
            if ($inSection) {
                if ($ct -match '^\S' -and $ct -ne '') { break }
                foreach ($m in [regex]::Matches($ct, $script:flagPattern)) { [void]$flags.Add($m.Groups[1].Value) }
            }
        }
        return $flags
    }
}

Describe "Command Discovery Parsing" {
    It "Detects category headers" {
        "Getting Started:" | Should -Match $script:categoryPattern
        "Work Items:" | Should -Match $script:categoryPattern
        "Views:" | Should -Match $script:categoryPattern
    }

    It "Rejects non-category lines" {
        "  set <id|pattern>     Set the active work item." | Should -Not -Match $script:categoryPattern
        "" | Should -Not -Match $script:categoryPattern
        "Usage: twig [command]" | Should -Not -Match $script:categoryPattern
    }

    It "Extracts simple command names from indented lines" {
        $line = "  init                 Initialize a new Twig workspace."
        $line -match $script:commandPattern | Should -BeTrue
        $Matches[1] | Should -Be "init"
    }

    It "Extracts compound command names" {
        $line = "  nav up               Navigate to the parent work item."
        $line -match $script:commandPattern | Should -BeTrue
        $Matches[1] | Should -Be "nav up"
    }

    It "Extracts commands with placeholder tokens" {
        $line = "  set <id|pattern>     Set the active work item."
        $line -match $script:commandPattern | Should -BeTrue
        $raw = $Matches[1]
        $raw | Should -Be "set <id|pattern>"

        # After stripping placeholders
        $bare = ($raw -replace $script:placeholderToken, '' -replace '\s+', ' ').Trim()
        $bare | Should -Be "set"
    }

    It "Extracts commands with optional-arg tokens" {
        $line = "  query [text]         Search work items by text."
        $line -match $script:commandPattern | Should -BeTrue
        $raw = $Matches[1]
        $bare = ($raw -replace $script:optionalArgToken, '' -replace '\s+', ' ').Trim()
        $bare | Should -Be "query"
    }

    It "Extracts commands with flag-variant tokens" {
        $line = "  seed new --editor    Create a seed via editor."
        $line -match $script:commandPattern | Should -BeTrue
        $raw = $Matches[1]
        $bare = ($raw -replace $script:flagVariantToken, '' -replace '\s+', ' ').Trim()
        $bare | Should -Be "seed new"
    }

    It "Extracts compound commands with multiple token types" {
        $line = "  seed new <title>     Create a new local seed."
        $line -match $script:commandPattern | Should -BeTrue
        $raw = $Matches[1]
        $bare = $raw -replace $script:placeholderToken, ''
        $bare = $bare -replace $script:optionalArgToken, ''
        $bare = $bare -replace $script:flagVariantToken, ''
        $bare = ($bare -replace '\s+', ' ').Trim()
        $bare | Should -Be "seed new"
    }

    It "Does not capture description text for commands with long padding" {
        $line = "  sprint               My sprint items, grouped by assignee.  (--all for team)"
        $line -match $script:commandPattern | Should -BeTrue
        $Matches[1] | Should -Be "sprint"
    }

    It "Deduplicates variant entries to the same bare command" {
        $commands = [System.Collections.Generic.HashSet[string]]::new()
        $lines = @(
            "  seed new <title>     Create a new local seed.",
            "  seed new --editor    Create a seed via editor."
        )
        foreach ($line in $lines) {
            if ($line -match $script:commandPattern) {
                $raw = $Matches[1]
                $bare = $raw -replace $script:placeholderToken, ''
                $bare = $bare -replace $script:optionalArgToken, ''
                $bare = $bare -replace $script:flagVariantToken, ''
                $bare = ($bare -replace '\s+', ' ').Trim()
                if ($bare -ne '') { [void]$commands.Add($bare) }
            }
        }
        $commands.Count | Should -Be 1
        $commands | Should -Contain "seed new"
    }
}

Describe "Flag Extraction" {
    It "Extracts long-form flags from Options lines" {
        $line = "  --force                Overwrite existing workspace configuration."
        $matches = [regex]::Matches($line, $script:flagPattern)
        $matches.Count | Should -Be 1
        $matches[0].Groups[1].Value | Should -Be "--force"
    }

    It "Extracts flags with hyphens in names" {
        $line = "  --no-refresh           Skip automatic refresh."
        $matches = [regex]::Matches($line, $script:flagPattern)
        $matches.Count | Should -Be 1
        $matches[0].Groups[1].Value | Should -Be "--no-refresh"
    }

    It "Extracts flag from short+long combined option" {
        $line = "  -o, --output <string>    Output format."
        $matches = [regex]::Matches($line, $script:flagPattern)
        $matches.Count | Should -Be 1
        $matches[0].Groups[1].Value | Should -Be "--output"
    }

    It "Extracts multiple flags from an example line" {
        $line = "  twig query --state Doing --top 50    Filter by state"
        $matches = [regex]::Matches($line, $script:flagPattern)
        $flags = $matches | ForEach-Object { $_.Groups[1].Value }
        $flags | Should -Contain "--state"
        $flags | Should -Contain "--top"
    }

    It "Does not match short flags" {
        $line = "  -o <string>    Output format."
        $matches = [regex]::Matches($line, $script:flagPattern)
        $matches.Count | Should -Be 0
    }
}

Describe "Options/Examples Section Parsing" {
    BeforeAll {
        $script:sampleHelp = @"
Usage: query [arguments...] [options...] [-h|--help] [--version]

Search work items.

Arguments:
  [0] <string?>    Free-text search.

Options:
  --title <string?>    Filter by title.
  --state <string?>    Filter by state.
  --top <int>          Max results.
  -o, --output <string>  Output format.

Examples:
  twig query "login bug"              Search title & description
  twig query --state Doing --top 50   Filter by state, limit results
"@
    }

    It "Extracts all long-form flags from Options section" {
        $flags = Get-SectionFlags ($script:sampleHelp -split "`n") 'Options'
        $flags | Should -Contain "--title"
        $flags | Should -Contain "--state"
        $flags | Should -Contain "--top"
        $flags | Should -Contain "--output"
        $flags.Count | Should -Be 4
    }

    It "Extracts flags from Examples section" {
        $flags = Get-SectionFlags ($script:sampleHelp -split "`n") 'Examples'
        $flags | Should -Contain "--state"
        $flags | Should -Contain "--top"
        $flags.Count | Should -Be 2
    }

    It "Detects no mismatches when all example flags are in Options" {
        $optionFlags = [System.Collections.Generic.HashSet[string]]::new()
        @("--title", "--state", "--top", "--output") | ForEach-Object { [void]$optionFlags.Add($_) }

        $exampleFlags = @("--state", "--top")
        $mismatches = @()
        foreach ($flag in $exampleFlags) {
            if (-not $optionFlags.Contains($flag)) {
                $mismatches += $flag
            }
        }
        $mismatches.Count | Should -Be 0
    }

    It "Detects mismatches when example uses unlisted flag" {
        $optionFlags = [System.Collections.Generic.HashSet[string]]::new()
        @("--title", "--state") | ForEach-Object { [void]$optionFlags.Add($_) }

        $exampleFlags = @("--state", "--bogus")
        $mismatches = @()
        foreach ($flag in $exampleFlags) {
            if (-not $optionFlags.Contains($flag)) {
                $mismatches += $flag
            }
        }
        $mismatches.Count | Should -Be 1
        $mismatches[0] | Should -Be "--bogus"
    }
}

Describe "Edge Cases" {
    It "Handles command with no Options section (empty flag set)" {
        $helpText = @"
Usage: version [-h|--help] [--version]

Show the current version.

Examples:
  twig version               Print the installed twig version
  twig version --output json  Output version info as JSON
"@
        $lines = $helpText -split "`n"
        $optionFlags  = Get-SectionFlags $lines 'Options'
        $exampleFlags = Get-SectionFlags $lines 'Examples'
        $optionFlags.Count | Should -Be 0
        $exampleFlags | Should -Contain "--output"
    }

    It "Handles command with no Examples section" {
        $helpText = @"
Usage: sync [options...] [-h|--help] [--version]

Flush pending changes then refresh from ADO.

Options:
  --force    Force full refresh.
"@
        $exampleFlags = Get-SectionFlags ($helpText -split "`n") 'Examples'
        $exampleFlags.Count | Should -Be 0
    }
}
