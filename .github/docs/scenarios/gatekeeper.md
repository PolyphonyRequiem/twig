# Gatekeeper Code Review

## Overview

The **Gatekeeper** scenario provides an automated multi-stage code review pipeline that reviews repository code against configurable guidelines. It uses parallel sub-agents to efficiently filter, batch, and review code at scale. The system enables teams to:

1. **Define Code Guidelines** — Maintain a library of anti-pattern documents that describe rules, detection instructions, and suggested fixes
2. **Automate Reviews** — Run a full pipeline that discovers guidelines, filters relevant files, batches work, and reviews code in parallel
3. **Generate Reports** — Produce structured JSON and Markdown violation reports for integration into PR workflows

## When to Use

Use this scenario when you need to:

- **Review code against a set of team guidelines** across an entire repository or specific changes
- **Automate code review** for pull requests, commit ranges, or staged changes
- **Scale reviews** across large codebases using parallel sub-agent execution
- **Generate actionable violation reports** with precise file locations and suggested fixes
- **Create new guideline documents** from code snippets or descriptions

## Prerequisites

### Required MCP Servers

This scenario uses one MCP server:

1. **Code Search MCP Server** — Enables code search and navigation across the codebase

### Required Software

- **VS Code** with GitHub Copilot extension
- **Node.js 18+** (for MCP server execution)
- **Python 3.8+** (for batch-files and merge-reports skills)

### Access Requirements

- Access to the repository you want to review
- A guidelines directory containing `.md` anti-pattern files

## What's Included

### Agents

- **[Gatekeeper](agents/Octane.Gatekeeper.agent.md)** — Orchestrator agent that drives the multi-stage review pipeline (discover, filter, batch, review, aggregate) by delegating to specialized sub-agents and tracking state in a session SQL database
- **[GatekeeperFilter](agents/Octane.GatekeeperFilter.agent.md)** — Specialized file-filter agent that analyzes guidelines and generates glob/regex filter specifications, writing results directly to the session database
- **[GatekeeperReviewer](agents/Octane.GatekeeperReviewer.agent.md)** — Expert code reviewer agent that analyzes code files against provided guidelines, identifies violations with precise locations, and outputs structured JSON results

### Prompts

- **[Octane.Gatekeeper.Review](prompts/Octane.Gatekeeper.Review.prompt.md)** — Prompt for running the Gatekeeper code review pipeline against anti-pattern guidelines to identify violations with precise locations and suggested fixes
- **[Octane.Gatekeeper.Generator](prompts/Octane.Gatekeeper.Generator.prompt.md)** — Prompt for generating new anti-pattern documents from code snippets or descriptions

### Skills

- **[batch-files](skills/batch-files/)** — Python script that groups file-guideline filter results into optimized review batches for parallel processing
- **[merge-reports](skills/merge-reports/)** — Python script that aggregates code review results from multiple batch reviews into a unified final JSON and Markdown report

## Pipeline Architecture

The Gatekeeper pipeline consists of five stages:

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Initialize  │────▶│    Filter    │────▶│    Batch     │────▶│    Review    │────▶│  Aggregate   │
│  (Stage 0)   │     │  (Stage 1)   │     │  (Stage 2)   │     │  (Stage 3)   │     │  (Stage 4)   │
└──────────────┘     └──────────────┘     └──────────────┘     └──────────────┘     └──────────────┘
  Discover            Filter agents        Group files into      Reviewer agents      Merge results
  guidelines,         determine which      optimized batches     evaluate code        into final
  create SQL          files match each     by shared guideline   against guidelines   JSON + MD
  tables              guideline            sets                  in parallel          report
```

### Stage 0 — Initialize

Creates SQL tables, discovers all guideline `.md` files, and registers them in the database.

### Stage 1 — Filter (Parallel)

Dispatches `gatekeeper-filter` sub-agents in parallel batches. Each filter agent reads its assigned guidelines, extracts scope metadata (glob patterns, content regex), and writes filter specifications to the SQL database.

### Stage 2 — Batch

Uses the `batch-files` skill or in-pipeline SQL logic to group files that share the same applicable guidelines into review batches (max ~10 files per batch).

### Stage 3 — Review (Parallel)

Dispatches `gatekeeper-reviewer` sub-agents in parallel. Each reviewer reads its assigned files and guidelines, performs independent per-guideline sweeps, and outputs structured JSON with violations, non-violations, and severity scores.

### Stage 4 — Aggregate

Uses the `merge-reports` skill to combine all batch results into:
- `output/final-review.json` — Machine-readable aggregated results
- `output/final-review-report.md` — Human-readable Markdown report

## Review Modes

| Mode | Trigger | Description |
|------|---------|-------------|
| **Full scan** | Default | Reviews all matching files in the repository |
| **Commit range** | `--commit-range <range>` | Reviews only files changed in the specified commit range |
| **Staged changes** | `--staged` | Reviews only staged (git index) changes |
| **Untracked changes** | `--untracked` | Reviews only untracked file changes |

## Configuration

Configure the pipeline via `.github/gkpconfig.yml` in the target repository:

```yaml
repo_root: .
guidelines_root: docs/guidelines   # Required: path to guideline .md files

folder_rules:
  '**':
    guidelines:
      - '**'
  'src/tests/**':
    guidelines:
      - 'test-*.md'
    exclude:
      - '**/helper/**'
```

| Key | Description | Default |
|-----|-------------|---------|
| `repo_root` | Path to the repository source | `.` |
| `guidelines_root` | Path to the guidelines directory | *required* |
| `folder_rules` | Per-folder guideline overrides | `{}` (all guidelines apply to all files) |

## Workflows

### Running a Code Review

1. **Configure guidelines** — Create `.md` anti-pattern files in a guidelines directory (see [Guideline Format](#guideline-format)) or configure `.github/gkpconfig.yml` with a `guidelines_root` path
2. **Run the review** — Invoke the Gatekeeper review prompt with the desired review mode:
   ```
   @gatekeeper
   gatekeeper review
   ```
3. **Wait for pipeline completion** — The orchestrator discovers guidelines, filters files, batches work, and dispatches parallel reviewers automatically
4. **Review the report** — Examine the generated `output/final-review-report.md` for violations and suggested fixes
5. **Fix violations** — Address findings and re-run on staged changes to verify:
   ```
   @gatekeeper
   gatekeeper review --staged
   ```

### Reviewing a Commit Range

```
@gatekeeper
gatekeeper review --commit-range HEAD~5..HEAD
```

### Reviewing Staged Changes

```
@gatekeeper
gatekeeper review --staged
```

### Generating a New Anti-Pattern

```
@workspace /Octane.Gatekeeper.Generator
Anti-Pattern Name: SQL Injection via String Concatenation
Code Snippet: var query = "SELECT * FROM users WHERE id = " + userId;
```

## Expected Output

After the pipeline completes, two report files are generated:

| File | Format | Description |
|------|--------|-------------|
| `output/final-review.json` | JSON | Machine-readable aggregated results with all violations, non-violations, and metadata |
| `output/final-review-report.md` | Markdown | Human-readable report with violations grouped by severity, file locations, and suggested fixes |

The pipeline also prints a summary to the chat showing the number of guidelines reviewed, files scanned, and violations found broken down by severity (Critical, High, Medium, Low, Informational).

## Guideline Format

Guidelines use the **Anti-Pattern** document format:

```markdown
# Anti-Pattern: Title

## Scope
(glob/regex patterns for file matching)

## Measurable Impact
(why this matters)

## Detection Instructions
(how to detect violations)

## Negative Example
(code demonstrating the anti-pattern)

## Positive Example
(corrected code)
```

## Best Practices

1. **Organize guidelines by category** — Group into folders like `security/`, `performance/`, `concurrency/`
2. **Use specific scopes** — Narrow file matching with precise glob/regex patterns to reduce noise
3. **List non-violations first** — In detection instructions, define acceptable patterns before violation patterns to reduce false positives
4. **Use folder rules** — Map specific guidelines to specific directories for targeted reviews
5. **Run incrementally** — Use commit range or staged modes for PR reviews instead of full scans

## Related Scenarios

- **[Test Analysis](../test-analysis/README.md)** — Analyze test results and coverage
- **[PR Insights](../pr-insights/README.md)** — Pull request analysis and insights

## Difficulty

**Advanced** — Requires understanding of:
- Code review anti-patterns and anti-pattern authoring
- Multi-agent orchestration and parallel pipelines
- SQL-based pipeline state management
- Git diff modes and file filtering

## Tags

`code-review` `anti-patterns` `automation` `quality` `parallel-pipeline`
