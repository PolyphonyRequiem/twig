# SDLC Toolkit

> **The most comprehensive agent skill in Octane.** Thirteen capabilities, four enterprise knowledge sources, 25 prompts — covering the entire software development lifecycle from code quality to developer onboarding to cross-repo coordination and meta-learning.

| What makes it unique | |
|---|---|
| 🔗 **Enterprise Knowledge** | Connects code to SharePoint, Teams, emails, and meetings via WorkIQ |
| 🎯 **Full SDLC Coverage** | Tech debt → Design review → Safety audit → Docs → Regression → Onboarding |
| 🤖 **Intelligent Routing** | Questions auto-route to the best source (code, enterprise, ADO, MS Learn) |
| 📊 **Output Contracts** | Every capability produces structured, actionable output — not generic advice |
| 🔄 **Session-Learned** | Five new capabilities derived from real multi-repo engineering sessions |

## Capabilities

1. **Tech Debt Discovery** — Find TODOs, FIXMEs, stale branches, outdated dependencies
2. **Design Review** — Validate design documents against 23 security, reliability, and architecture rules
3. **Safety Audit** — Prompt injection testing, PII detection, SBOM generation
4. **Artifact Curation** — Gap analysis, ADR creation, semantic knowledge search
5. **Regression Oracle** — Predict and prevent regressions using historical bug patterns
6. **Repository Onboarding** — Generate onboarding guides from code + enterprise knowledge (WorkIQ, ADO, MS Learn)
7. **Onboarding Buddy** — Interactive Q&A with intelligent source routing
8. **Cross-Repo Impact Analysis** — Analyze blast radius of changes across all ADO repos with dependency mapping and risk scoring
9. **Document Generation** — Generate executive briefings, weekly recaps, and blog posts from live system data
10. **Transcript Processing** — Convert meeting recordings into structured decisions, action items, and spec updates
11. **Drift Detection** — Detect configuration drift between source repo, Octane scenario, and Agency plugin
12. **Multi-Repo Coordination** — Coordinate dependency-ordered changes and cascading PRs across repos
13. **Session Analysis** — Meta-learning from agent session history to improve agent design

## Prerequisites

- Python 3.10+
- SDLC Toolkit package:
  ```bash
  pip install git+https://github.com/azure-core/sdlc-toolkit.git
  ```

## Installation

```bash
octane install sdlc-toolkit
```

## Agent

| Agent | Purpose |
|-------|---------|
| [SdlcSpecialist](agents/Octane.SdlcSpecialist.agent.md) | Unified agent for all SDLC tasks |

## Quick Start

### Tech Debt Discovery

```bash
# Quick summary
/Octane.SDLCToolkit.TechDebtSummary

# Find hotspots
/Octane.SDLCToolkit.TechDebtHotspots

# Full scan
/Octane.SDLCToolkit.FindTechDebt ./src
```

### Design Review

```bash
# Full design review
/Octane.SDLCToolkit.DesignReview design.md

# Quick check for blocking issues
/Octane.SDLCToolkit.QuickDesignCheck design.md

# List validation rules
/Octane.SDLCToolkit.DesignRules
```

### Safety Audit

```bash
# Comprehensive safety check
/Octane.SDLCToolkit.SafetyCheck my-agent

# Test for prompt injection
/Octane.SDLCToolkit.InjectionTest my-agent

# Scan for PII
/Octane.SDLCToolkit.PIIScan ./src

# Generate SBOM
/Octane.SDLCToolkit.SBOM ./project
```

### Artifact Curation

```bash
# Analyze documentation gaps
/Octane.SDLCToolkit.CurateStart

# Index repository for search
/Octane.SDLCToolkit.KnowledgeIndex

# Search knowledge base
/Octane.SDLCToolkit.KnowledgeSearch "authentication"
```

### Regression Oracle

```bash
# Ingest historical bugs
/Octane.SDLCToolkit.IngestBugs bugs.json

# Analyze PR for regression risk
/Octane.SDLCToolkit.OracleAnalyze src/auth/session.py --title "Fix session timeout"
```

### Repository Onboarding

```bash
# Generate comprehensive onboarding guide (code + enterprise knowledge)
/Octane.SDLCToolkit.RepoOverview

# Create a PR with the guide for SME review
/Octane.SDLCToolkit.OnboardingPR
```

### Onboarding Buddy (Interactive Q&A)

```bash
# Ask any question — the buddy routes to the best source
/Octane.SDLCToolkit.OnboardingBuddy How does the auth middleware work?
/Octane.SDLCToolkit.OnboardingBuddy Why did we choose CosmosDB?
/Octane.SDLCToolkit.OnboardingBuddy Who owns the billing service?
/Octane.SDLCToolkit.OnboardingBuddy What are the SLA requirements?
```

### Cross-Repo Impact Analysis

```bash
# Full impact analysis — searches all ADO repos, maps dependencies, scores risk
/Octane.SDLCToolkit.ImpactAnalysis I need to change the auth token format from JWT to opaque tokens

# Quick scan — lightweight "which repos are affected?"
/Octane.SDLCToolkit.ImpactQuickScan We're deprecating the ServiceBus retry helper in shared-utils
```

### Document Generation

```bash
# Generate executive briefing from recent activity
/Octane.SDLCToolkit.GenerateDocument Create a 2-page executive briefing on the last 2 weeks of progress
```

### Transcript Processing

```bash
# Process meeting transcript into engineering artifacts
/Octane.SDLCToolkit.ProcessTranscript [paste or path to VTT file]
```

### Drift Detection & Cross-Repo Coordination

```bash
# Check sync status across source, Octane, and Playground
/Octane.SDLCToolkit.DetectDrift

# Coordinate changes across multiple repos
/Octane.SDLCToolkit.CoordinateRepos I need to add a new agent across sdlc-toolkit, octane, and playground

# Analyze agent session for improvement opportunities
/Octane.SDLCToolkit.AnalyzeSession
```

## Capabilities by Area

### Tech Debt Discovery

- **Code Markers**: TODO, FIXME, HACK, XXX, TEMP, DEPRECATED
- **Git Analysis**: Stale branches, debt commits, code churn
- **Dependencies**: Outdated packages, unpinned versions
- **ADO Integration**: Link debt to work items

### Design Review

- **Security Rules**: Auth, encryption, input validation (8 rules)
- **Reliability Rules**: Retries, timeouts, circuit breakers (6 rules)
- **Architecture Rules**: Scalability, caching, observability (5 rules)
- **AI Analysis**: Semantic understanding with Azure OpenAI

### Safety Audit

- **Prompt Injection**: 17+ attack pattern tests
- **PII Detection**: SSN, credit cards, emails, phones
- **SBOM**: Dependency tracking and vulnerability scanning
- **Policy Compliance**: Organizational safety policies

### Artifact Curation

- **Gap Analysis**: Identify missing documentation
- **ADR Creation**: Architecture Decision Records
- **Glossary**: Domain terminology
- **Knowledge Search**: Semantic search across docs

### Regression Oracle

- **Bug Patterns**: Learn from historical bugs
- **Risk Prediction**: Score PR changes
- **Test Suggestions**: Recommend tests based on patterns
- **Hotspot Detection**: Files with bug history

### Repository Onboarding

- **Multi-Source Guide**: Combines codebase analysis with enterprise knowledge (design docs, meeting notes, work items)
- **Architecture Discovery**: Auto-generates architecture diagrams and component maps
- **Decision History**: Extracts "why we chose X" from SharePoint, Teams, and email via WorkIQ
- **Ownership Map**: Discovers subject matter experts and component owners
- **PR Automation**: Creates onboarding guide PRs with auto-discovered SME reviewers

### Onboarding Buddy (Interactive Q&A)

- **Intelligent Routing**: Routes questions to the best source (code, enterprise docs, official docs, work items)
- **People Discovery**: "Who owns X?" via WorkIQ
- **Decision Context**: "Why did we choose X?" from design docs and meetings
- **Combined Answers**: Merges multiple sources for comprehensive responses
- **Follow-up Suggestions**: Recommends related questions to explore

### Cross-Repo Impact Analysis

- **Cross-Repo Search**: Searches all accessible ADO repos for references to affected APIs, packages, config
- **Dependency Mapping**: Builds repo-to-repo dependency graph with Mermaid diagrams
- **Impact Classification**: API contract, shared package, config reference, pipeline dependency, data contract, transitive
- **Risk Scoring**: Per-repo risk based on API breakage potential, test coverage, staleness, complexity, depth
- **Owner Discovery**: Identifies SMEs via WorkIQ people search and ADO PR/work item analysis
- **Implementation Plan**: Sequenced change order based on dependency graph

## Skills Reference

This scenario includes comprehensive skills documentation:

- [SDLC Toolkit Skill](skills/sdlc-toolkit/SKILL.md) - Unified skill reference
- [Security Rules](skills/sdlc-toolkit/references/security-rules.md)
- [Reliability Rules](skills/sdlc-toolkit/references/reliability-rules.md)
- [Architecture Rules](skills/sdlc-toolkit/references/architecture-rules.md)
- [Attack Patterns](skills/sdlc-toolkit/references/attack-patterns.md)
- [PII Patterns](skills/sdlc-toolkit/references/pii-patterns.md)

## MCP Servers

| Server | Purpose |
|--------|---------|
| code-search | Code navigation and search |
| ado | Azure DevOps work item integration |
| ms-learn | Microsoft documentation search |
| work-iq | Enterprise knowledge (SharePoint, Teams, emails, meetings, people) |

## Contributing

See [CONTRIBUTING.md](../../../CONTRIBUTING.md) for guidelines on improving this scenario.

## Differentiation from Repository Overview Scenario

The **sdlc-toolkit** `RepoOverview` and `OnboardingBuddy` capabilities overlap with the standalone [repository-overview](../repository-overview/README.md) scenario. Use this guide to choose:

| Use Case | Use |
|----------|-----|
| **Quick repo overview for context hydration** | `repository-overview` scenario — lightweight, Planner-only |
| **Full onboarding guide with enterprise knowledge** | `sdlc-toolkit` `/Octane.SDLCToolkit.RepoOverview` — combines code + WorkIQ + ADO + MS Learn |
| **Interactive Q&A for new developers** | `sdlc-toolkit` `/Octane.SDLCToolkit.OnboardingBuddy` — routes questions to best source |

The sdlc-toolkit version is richer (adds ownership maps, decision history, operational context from enterprise sources) while the standalone scenario is simpler and faster for code-only analysis.
