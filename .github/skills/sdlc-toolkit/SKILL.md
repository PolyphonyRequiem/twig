---
name: sdlc-toolkit
description: >
  Full-lifecycle SDLC toolkit: find tech debt, review designs, audit safety,
  curate docs, predict regressions, onboard developers, analyze cross-repo impact,
  and answer questions across code + enterprise knowledge (SharePoint, Teams, ADO, MS Learn).
  Activate for: code quality, security review, design review, onboarding,
  documentation, tech debt, regression, safety audit, PII scan, knowledge search,
  impact analysis, cross-repo, blast radius, dependency analysis.
---

# SDLC Toolkit

Eight capabilities, one skill. The only agent skill that spans the entire SDLC
**and** connects code to enterprise knowledge (WorkIQ, ADO, MS Learn).

| # | Capability | Trigger Phrases |
|---|-----------|----------------|
| 1 | [Tech Debt Discovery](#tech-debt-discovery) | "find tech debt", "code health", "TODO scan" |
| 2 | [Design Review](#design-review) | "review design", "security check", "is this spec ready" |
| 3 | [Safety Audit](#safety-audit) | "safety check", "PII scan", "prompt injection test" |
| 4 | [Artifact Curation](#artifact-curation) | "curate docs", "documentation gaps", "knowledge search" |
| 5 | [Regression Oracle](#regression-oracle) | "regression risk", "PR risk", "bug patterns" |
| 6 | [Repository Onboarding](#repository-onboarding) | "onboarding guide", "repo overview", "new developer" |
| 7 | [Onboarding Buddy](#onboarding-buddy) | "who owns", "why did we choose", "how does X work" |
| 8 | [Cross-Repo Impact Analysis](#cross-repo-impact-analysis) | "impact analysis", "which repos are affected", "blast radius", "what will break" |

## Setup

```bash
pip install git+https://github.com/azure-core/sdlc-toolkit.git
sdlc-toolkit --help   # verify
```

Full guide → [references/setup.md](references/setup.md) · All commands → [references/skills-agent.md](references/skills-agent.md)

---

## Tech Debt Discovery

**What it does:** Scans for code markers (TODO/FIXME/HACK), stale branches, outdated deps, and code churn hotspots.

**Commands:**
```bash
sdlc-toolkit techdebt-summary .          # quick overview
sdlc-toolkit techdebt-hotspots . --top 10  # worst files
sdlc-toolkit techdebt-scan ./src         # full scan
```

**Output contract:**
- Summary with total count and severity breakdown
- Top items ranked by severity (Critical → High → Medium → Low)
- Actionable recommendation for each high-severity item

<details><summary>Marker reference</summary>

| Marker | Severity | Meaning |
|--------|----------|---------|
| FIXME | High | Known bug, needs fix |
| HACK | High | Workaround, needs refactor |
| TODO | Medium | Planned work |
| XXX | Medium | Warning, review needed |
| TEMP | Medium | Temporary code |
| DEPRECATED | Low | Outdated, remove soon |
| OPTIMIZE | Low | Performance improvement |

</details>

**Example interaction:**
> **User:** "How healthy is this codebase?"
> **Agent:** Runs `techdebt-summary`, reports 12 items (3 FIXME, 2 HACK, 7 TODO), highlights the 3 FIXMEs with file paths and recommendations, suggests `techdebt-hotspots` for deeper analysis.

---

## Design Review

**What it does:** Validates design documents against 23 rules across security, reliability, architecture, and compliance.

**Commands:**
```bash
sdlc-toolkit design-review design.md                # full review
sdlc-toolkit design-rules --category security       # list rules
```

**Output contract:**
- Pass/fail verdict with issue count by severity
- Each finding: rule ID, severity, description, recommendation
- Positive findings ("what's done well") section

**Quality gates:**
| Severity | Meaning | Action |
|----------|---------|--------|
| Critical | Blocks implementation | Must fix before proceeding |
| High | Fix before production | Address in current sprint |
| Medium | Address in sprint | Schedule for backlog |
| Low | Nice to have | Optional improvement |

Rule details → [Security](references/security-rules.md) · [Reliability](references/reliability-rules.md) · [Architecture](references/architecture-rules.md)

**Example interaction:**
> **User:** "Is this spec ready for implementation?"
> **Agent:** Reviews the doc, finds 1 Critical (missing auth for /admin endpoint), 2 High (no retry on external calls, missing timeout config). Verdict: FAIL — 1 blocking issue. Lists each with specific fix guidance.

---

## Safety Audit

**What it does:** Tests agents for prompt injection resistance, PII exposure, and dependency vulnerabilities. Generates SBOMs.

**Commands:**
```bash
sdlc-toolkit safety-check my-agent --code ./src   # comprehensive
sdlc-toolkit injection-test my-agent               # 17+ attack patterns
sdlc-toolkit pii-scan ./src                        # sensitive data
sdlc-toolkit sbom ./project                        # dependency bill
```

**Output contract:**
- PASS / WARN / FAIL verdict
- Per-test results with attack category and outcome
- PII findings with file, line, type, and severity
- Remediation steps for each failure

<details><summary>Attack categories tested</summary>

Ignore Previous Instructions · Role Playing · Encoding/Obfuscation · Context Manipulation · Token Smuggling · 12 more → [Attack Patterns](references/attack-patterns.md)

</details>

<details><summary>PII types detected</summary>

SSN · Credit Card · Email · Phone · API Keys · more → [PII Patterns](references/pii-patterns.md)

</details>

---

## Artifact Curation

**What it does:** Analyzes documentation gaps, creates high-impact artifacts (ADRs, glossaries, context docs), and enables semantic search.

**Commands:**
```bash
sdlc-toolkit curate .                      # gap analysis
sdlc-toolkit kb-index .                    # index for search
sdlc-toolkit kb-search "authentication"    # semantic search
```

**Output contract:**
- Existing artifacts inventory with type and path
- Gap list ranked by Copilot impact (High → Medium → Low)
- Specific recommendation for the #1 priority gap

| Artifact Type | Copilot Impact | Why |
|--------------|---------------|-----|
| context-doc | 🔴 High | Loaded automatically, sets project context |
| glossary | 🔴 High | Improves naming and terminology |
| adr | 🔴 High | Explains architectural decisions |
| coding-standard | 🟡 Medium | Ensures consistent code style |
| api-spec | 🟡 Medium | Correct API usage |

---

## Regression Oracle

**What it does:** Learns from historical bugs to predict regression risk in PRs and suggest preventive tests.

**Commands:**
```bash
sdlc-toolkit oracle-ingest bugs.json                          # load history
sdlc-toolkit oracle-analyze file1.py file2.py --title "PR"    # assess risk
sdlc-toolkit oracle-summary                                    # pattern overview
```

**Output contract:**
- Risk level with confidence score (Critical/High/Medium/Low)
- Related historical bugs with file overlap percentage
- Top 3 specific recommendations to reduce risk
- Suggested tests based on detected patterns

**Example interaction:**
> **User:** "Is this PR safe to merge?"
> **Agent:** Analyzes `src/auth/session.py` changes against 47 historical bugs. Risk: **High** (82% confidence) — 3 prior bugs in auth/session around timeout handling. Recommends: add timeout boundary test, check null session edge case, verify token refresh path.

---

## Repository Onboarding

**What it does:** Generates comprehensive onboarding guides by combining codebase analysis with enterprise knowledge from 4 sources.

**Prompts:**
```
/Octane.SDLCToolkit.RepoOverview          # generate guide
/Octane.SDLCToolkit.OnboardingPR          # create PR for SME review
```

**Knowledge sources:**

| Source | MCP Tools | Content |
|--------|-----------|---------|
| Codebase | `code-search/*` | Architecture, APIs, dependencies, patterns |
| Enterprise | `work-iq/*` | Design reviews, tech specs, meeting notes, people |
| Official Docs | `ms-learn/*` | Azure services, SDKs, best practices |
| Work Items | `ado/*` | PRs, wikis, pipelines, deployment history |

**Output contract (RepoOverview):**
- Architecture overview with component diagram
- API contracts and key data flows
- Azure infrastructure and deployment topology
- Decision history ("why we chose X")
- Ownership map with SME contacts
- Links to source documents

---

## Onboarding Buddy

**What it does:** Interactive Q&A that routes developer questions to the best knowledge source automatically.

**Prompt:**
```
/Octane.SDLCToolkit.OnboardingBuddy <your question>
```

**Question routing:**

| Question Type | Primary Source | Example |
|---------------|---------------|---------|
| Code / implementation | `code-search/*` | "How does the auth middleware work?" |
| Decisions / rationale | `work-iq/*` | "Why did we choose CosmosDB?" |
| People / ownership | `work-iq/*` | "Who owns the billing service?" |
| Azure / Microsoft | `ms-learn/*` | "How do I configure App Service slots?" |
| Process / deployment | `ado/*` | "How do we deploy to production?" |

**Output contract:**
- Direct answer citing the source(s) used
- Relevant links to source documents
- 2-3 suggested follow-up questions

---

## Cross-Repo Impact Analysis

**What it does:** Analyzes a proposed change or feature across all accessible ADO repositories to identify impacted repos, files, APIs, dependency chains, and owners — then produces a risk-scored impact report with a sequenced implementation plan.

**Prompts:**
```
/Octane.SDLCToolkit.ImpactAnalysis          # full cross-repo analysis
/Octane.SDLCToolkit.ImpactQuickScan         # lightweight "which repos?"
```

**Knowledge sources:**

| Source | MCP Tools | What It Provides |
|--------|-----------|-----------------|
| ADO Code Search | `ado/*` | Cross-repo search for interfaces, API contracts, shared packages, config |
| ADO Work Items | `ado/*` | Related work items, wiki docs, pipeline definitions |
| Enterprise | `work-iq/*` | Design docs mentioning the feature, SME contacts, decision history |
| Codebase | `code-search/*` | Current repo context (starting point for analysis) |
| Official Docs | `ms-learn/*` | Docs for affected Azure services/SDKs |

**Workflow (Full Analysis):**

1. **Scope Definition** — Parse the problem/feature description, extract key terms (APIs, interfaces, packages, config keys, service names)
2. **Current Repo Analysis** — Understand the change in context using `code-search/*`
3. **Cross-Repo Search** — Use `ado/*` to search across all accessible repos for references to affected APIs, shared package imports, config referencing affected services, and pipeline dependencies
4. **Dependency Mapping** — Build a dependency graph from search results showing repo-to-repo relationships
5. **Owner Discovery** — Use `work-iq/*` and `ado/*` to identify SMEs for each affected area
6. **Risk Assessment** — Score each impacted repo on API breakage potential, test coverage, code staleness, and change complexity
7. **Report Generation** — Synthesize into the structured impact report
8. **Recommendations** — Suggest sequenced implementation plan based on dependency order

**Output contract (ImpactAnalysis):**
- Summary with scope, repo count, and overall risk level
- Per-repo impact table: files, impact type (API contract / config / dependency / shared lib), description, and owner
- Dependency chain diagram (Mermaid)
- Risk assessment matrix with factors and levels
- Sequenced implementation plan (which repo to change first)
- SME contact list per affected area

**Output contract (ImpactQuickScan):**
- List of affected repos with match count and match type
- One-line summary per repo explaining why it's impacted
- Suggested next step: run full analysis on high-match repos

<details><summary>Impact types detected</summary>

| Impact Type | What It Means | Detection Method |
|-------------|---------------|-----------------|
| API contract | Repo consumes/implements the affected API | Interface/class/method search via ADO |
| Shared package | Repo imports the affected package/library | Dependency file search (package.json, .csproj, requirements.txt) |
| Config reference | Repo references the affected service/endpoint in config | Config file search (appsettings, env, yaml) |
| Pipeline dependency | Repo's CI/CD depends on the affected component | Pipeline definition search |
| Data contract | Repo reads/writes the affected data model | Schema/model class search |
| Transitive | Repo doesn't directly depend, but depends on an impacted repo | Dependency chain analysis |

</details>

**Example interaction:**
> **User:** "I need to change the authentication token format from JWT to opaque tokens. What repos will be impacted?"
> **Agent:** Searches across ADO for JWT references, token validation code, auth middleware, and config. Finds 7 repos impacted: 3 consume the auth SDK directly (API contract change), 2 reference the token format in config, 1 has a pipeline that validates tokens, 1 is a transitive dependency. Risk: **High** — the auth SDK is consumed by 3 production services. Recommends: update auth SDK first, then consumer repos in parallel, pipeline repo last. Identifies 4 SMEs from WorkIQ design docs and ADO PR history.

---

## Troubleshooting

| Problem | Cause | Fix |
|---------|-------|-----|
| `sdlc-toolkit: command not found` | Not installed | `pip install git+https://github.com/azure-core/sdlc-toolkit.git` |
| WorkIQ returns no results | Tenant consent needed | Admin must approve WorkIQ app; see [WorkIQ setup](https://github.com/microsoft/workiq) |
| Design review gives generic results | No Azure OpenAI configured | Set `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` |
| ADO commands fail | Missing PAT | Set `ADO_ORG`, `ADO_PROJECT`, `ADO_PAT` environment variables |
| Oracle shows low confidence | Insufficient bug data | Ingest more history: `sdlc-toolkit oracle-ingest bugs.json` |

## Environment Variables

```bash
# Azure OpenAI (for AI-powered design review)
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-key
AZURE_OPENAI_DEPLOYMENT=gpt-4

# Azure DevOps
ADO_ORG=your-org
ADO_PROJECT=your-project
ADO_PAT=your-pat
```

## References

- [Security Rules](references/security-rules.md) · [Reliability Rules](references/reliability-rules.md) · [Architecture Rules](references/architecture-rules.md)
- [Attack Patterns](references/attack-patterns.md) · [PII Patterns](references/pii-patterns.md)
- [Setup Guide](references/setup.md) · [CLI Reference](references/skills-agent.md)
