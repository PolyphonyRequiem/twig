# AI Native Daemon — Governed Agent Fleet

A production-grade fleet of 16 AI agents that continuously watches your GitHub and Azure DevOps repos,
triages issues/work items, reviews PRs, generates tests, processes meeting transcripts, coordinates
cross-repo operations, and manages releases — with enterprise governance, trust scoring, parallel
execution, and human-in-the-loop approval.

## When to Use

- **Continuous repo monitoring**: Automatically triage issues/work items, review PRs, and scan for security issues
- **SDLC automation**: Issue → Spec → Copilot implements → PR Review → Merge pipeline
- **Enterprise governance**: Trust scoring, circuit breakers, approval queues, cost tracking
- **Team dashboard**: Real-time visibility into what agents are doing across all repos
- **Multi-platform**: Works with GitHub and Azure DevOps repos side by side

## Prerequisites

- Python 3.11+
- GitHub CLI (`gh`) with Copilot extension
- GitHub Copilot subscription
- Agent SRE package: `pip install git+https://github.com/azure-core/agent-sre.git`
- For ADO repos: `AZURE_DEVOPS_PAT` env var or Azure CLI (`az`)

## Quick Start

```bash
# Install
pip install git+https://github.com/azure-core/ai-native-team.git

# Start watching GitHub repos
python -m daemon watch --repos your-org/your-repo --dashboard 7070

# Start watching ADO repos
export AZURE_DEVOPS_PAT=your-pat ADO_ORG=your-org ADO_PROJECT=your-project ADO_REPOS=repo-a
python -m daemon watch --dashboard 7070

# Open http://localhost:7070
```

## Agent Fleet

| Agent | What It Does |
|-------|-------------|
| **code-reviewer** | Reviews PRs for bugs, security, performance, pattern compliance |
| **security-scanner** | OWASP Top 10, secret detection, dependency CVEs, IaC misconfig |
| **test-generator** | Framework-aware test scaffolding for untested code |
| **spec-drafter** | Technical specifications from issue descriptions, with transcript intake |
| **implementer** | Addresses review feedback, implements changes (Windows-aware) |
| **release-notes** | Categorized changelogs from merged PRs |
| **repo-health** | Health score across 6 dimensions (including cross-repo drift) |
| **iac-validator** | Validates Bicep, Terraform, ARM, GitHub Actions |
| **refactor-advisor** | Identifies complexity, duplication, dead code |
| **fleet-orchestrator** | Coordinates multi-agent and cross-repo pipeline execution |
| **researcher** | Proactive research with mandatory prior-art search |
| **transcript-processor** | Meeting VTT/SRT → structured decisions, actions, concerns |
| **windows-compat-checker** | Pre-flight Windows-specific issue detection |
| **multi-repo-coordinator** | Cross-repo auth, dependency ordering, cascading PRs |
| **document-generator** | Formatted docs (briefings, blogs, reports) from live data |
| **merge-conflict-resolver** | Pre-push conflict detection and safe auto-rebase |

## Prompts

| Prompt | Description |
|--------|-------------|
| `/Octane.AINativeDaemon.StartDaemon` | Start the daemon watching specified repos |
| `/Octane.AINativeDaemon.ReviewPR` | Trigger a code review on a specific PR |
| `/Octane.AINativeDaemon.SecurityScan` | Run security scan on a repo or PR |
| `/Octane.AINativeDaemon.GenerateTests` | Generate tests for untested code |
| `/Octane.AINativeDaemon.DraftSpec` | Draft a technical spec from an issue |
| `/Octane.AINativeDaemon.FleetStatus` | Show agent fleet status and health |
| `/Octane.AINativeDaemon.SessionHealth` | Show Copilot session health and DORA metrics |
| `/Octane.AINativeDaemon.ProcessTranscript` | Extract decisions and actions from meeting transcripts |
| `/Octane.AINativeDaemon.WindowsCompat` | Run Windows compatibility pre-flight checks |
| `/Octane.AINativeDaemon.MultiRepoSync` | Coordinate cross-repo drift checks and cascading PRs |
| `/Octane.AINativeDaemon.GenerateDocument` | Generate formatted docs from live system data |
| `/Octane.AINativeDaemon.ResolveConflicts` | Pre-push merge conflict detection and resolution |

## Features

- **Parallel Execution** — Agents run concurrently via ThreadPoolExecutor
- **Autonomous Retry** — Transient failures auto-retry with backoff
- **Session Persistence** — SQLite with WAL mode, survives restarts
- **Governance Gates** — Trust scoring, token budgets, policy modes
- **Observability** — Session health, cost analytics, and DORA metrics via Agent SRE MCP server
- **Live Dashboard** — SSE-powered with command palette (Ctrl+K), keyboard shortcuts
- **Teams Notifications** — Adaptive cards for failures, approvals, completions
- **Copilot Integration** — Uses GitHub Copilot CLI (no separate API billing)
- **Cross-Repo Coordination** — Multi-repo auth switching, dependency ordering, cascading PRs
- **Windows Support** — Pre-flight compatibility scanning, `az.cmd` handling, path normalization
- **Transcript Processing** — Meeting recordings → structured decisions, actions, architecture changes
- **ADC Integration** — Agent Dev Compute microVM execution with Azure CLI auth

## Links

- [Repository](https://github.com/azure-core/ai-native-team)
- [Agency Plugin](https://github.com/agency-microsoft/playground/tree/main/plugins/ai-native-team)
- [Architecture Docs](https://github.com/azure-core/ai-native-team/blob/main/docs/ARCHITECTURE.md)

## Workflows

1. **Continuous Monitoring** — Start the daemon, it polls repos and dispatches agents automatically
2. **On-Demand Review** — Trigger a code review or security scan on a specific PR
3. **SDLC Pipeline** — Issue → Spec → Implementation → Review → Merge, orchestrated by the fleet
4. **Fleet Management** — Check status, approve/reject actions, manage circuit breakers

## Example Prompts

```shell
# Start watching repos
/Octane.AINativeDaemon.StartDaemon

# Review a specific PR
/Octane.AINativeDaemon.ReviewPR

# Run a security scan
/Octane.AINativeDaemon.SecurityScan

# Generate tests for untested code
/Octane.AINativeDaemon.GenerateTests

# Draft a spec from an issue
/Octane.AINativeDaemon.DraftSpec

# Check fleet health
/Octane.AINativeDaemon.FleetStatus

# Show Copilot session health and cost
/Octane.AINativeDaemon.SessionHealth

# Process a meeting transcript
/Octane.AINativeDaemon.ProcessTranscript

# Check Windows compatibility
/Octane.AINativeDaemon.WindowsCompat

# Sync agents across repos
/Octane.AINativeDaemon.MultiRepoSync

# Generate an executive briefing
/Octane.AINativeDaemon.GenerateDocument

# Resolve merge conflicts before push
/Octane.AINativeDaemon.ResolveConflicts
```

## Expected Output

Each prompt returns structured output tailored to its task:
- **StartDaemon**: Confirmation with agent count, watched repos, and dashboard URL
- **ReviewPR**: Findings grouped by severity (critical → warning → nit)
- **SecurityScan**: Categorized findings (secrets, injection, auth, dependencies, infrastructure)
- **GenerateTests**: Detected framework, untested functions, and generated test scaffolding
- **DraftSpec**: Structured spec with Overview, Architecture, Implementation, Testing, Rollout
- **FleetStatus**: Agent health summary, success rate, SLO compliance, recent tasks
