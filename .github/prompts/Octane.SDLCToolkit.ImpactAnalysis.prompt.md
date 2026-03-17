---
agent: SdlcSpecialist
description: Analyze cross-repo impact of a proposed change across all accessible ADO repositories.
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo', 'code-search/*', 'work-iq/*', 'ado/*', 'ms-learn/*']
model: Claude Opus 4.6 (copilot)
---

## PRIMARY DIRECTIVE

Perform a **Cross-Repo Impact Analysis** for the proposed change described by the user. Systematically search all accessible Azure DevOps repositories to identify every repo, file, API, and team that will be affected — then produce a risk-scored impact report with a sequenced implementation plan.

This is NOT a single-repo analysis. You must search **across repositories** using ADO code search to find consumers, dependents, and transitive impacts.

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Scope Definition**
   - Parse the user's change description to extract key search terms:
     - API names, interface names, class names, method signatures
     - Package/library names (NuGet, npm, pip packages)
     - Configuration keys, connection strings, service endpoints
     - Service names, queue names, topic names
   - Use `code-search/*` tools to understand the change in the current repository context
   - Identify the primary artifacts being changed (APIs, contracts, schemas, config)
   - Respond with: extracted search terms and initial scope assessment

2. **Cross-Repo Code Search**
   - Use the #runSubagent tool to invoke parallel sub-agents, one for each search category:
     - **API Consumer Search sub-agent**: Use `ado/*` tools to search across all ADO repos for references to the affected interfaces, classes, and methods. Use search patterns like `class:InterfaceName`, `ref:MethodName`, and import statements. Record: repo, file path, line, match context.
     - **Package Dependency Search sub-agent**: Use `ado/*` tools to search dependency files (package.json, *.csproj, requirements.txt, pyproject.toml, go.mod) for references to the affected package/library. Record: repo, dependency file, version pinned.
     - **Config & Infrastructure Search sub-agent**: Use `ado/*` tools to search config files (appsettings*.json, *.yaml, *.env, web.config, pipeline YAML) for references to affected service endpoints, queue names, connection strings, or feature flags. Also search pipeline definitions for triggers on the affected repo. Record: repo, config file, key matched.

3. **Dependency Chain Mapping**
   - Aggregate results from all search sub-agents
   - Classify each match by impact type:
     - **API contract** — Repo implements or consumes the affected interface/API
     - **Shared package** — Repo imports the affected package/library
     - **Config reference** — Repo references the affected service/endpoint in config
     - **Pipeline dependency** — Repo's CI/CD triggers on or depends on the affected repo
     - **Data contract** — Repo reads/writes the affected data model/schema
   - Build a dependency graph: which repos depend on which, and through what mechanism
   - Identify **transitive dependencies** — repos that don't directly reference the changed code but depend on a repo that does
   - Generate a Mermaid diagram of the dependency chain

4. **Owner & Context Discovery**
   - Use the #runSubagent tool to invoke two parallel sub-agents:
     - **SME Discovery sub-agent**: Use `work-iq/*` tools to search for people associated with each impacted repo — authors of design docs, participants in architecture discussions, active contributors. Also use `ado/*` tools to identify recent PR authors and work item assignees for each affected file/module.
     - **Design Context sub-agent**: Use `work-iq/*` tools to search for design documents, tech specs, and decision records related to the affected components. This provides context for WHY the current design exists and informs the risk assessment. Also use `ado/*` tools to find related wiki pages.

5. **Risk Assessment**
   - For each impacted repository, score risk based on:
     - **API breakage potential**: Is this a breaking change to a consumed API? How many consumers?
     - **Test coverage gap**: Does the consumer repo have tests covering the affected integration?
     - **Code staleness**: When was the affected code in the consumer repo last updated?
     - **Change complexity**: How extensive are the required changes in the consumer repo?
     - **Dependency depth**: Is this a direct or transitive dependency?
   - Assign an overall risk level: Critical / High / Medium / Low
   - Identify the highest-risk repos and explain why

6. **Report Generation**
   - Synthesize all findings into the mandatory report template (below)
   - Include the Mermaid dependency diagram
   - Sequence the implementation plan based on dependency order:
     - Change the most upstream repo first (the one others depend on)
     - Then change direct consumers in parallel where possible
     - Change transitive dependents last
   - Include SME contacts for each affected area

7. **Review and Validate**
   - Use the #runSubagent tool to invoke a review sub-agent that:
     - Cross-checks that all search results are accounted for in the report
     - Validates that the dependency chain is logically consistent
     - Verifies that the implementation plan respects dependency ordering
     - Checks for any missed transitive dependencies
     - Validates risk levels against the evidence

## MANDATORY REPORT TEMPLATE

```markdown
# Cross-Repo Impact Analysis: {change title}

**Date:** {YYYY-MM-DD}
**Analyst:** SDLC Toolkit (automated)
**Scope:** {one-line description of the proposed change}

## Summary

| Metric | Value |
|--------|-------|
| Repos scanned | {count} |
| Repos impacted | {count} |
| Files affected | {count} across {repo count} repos |
| Overall risk | {Critical / High / Medium / Low} |
| Estimated coordination | {count} teams |

## Impacted Repositories

### {repo-name} — Risk: {Critical/High/Medium/Low}

**Why impacted:** {one-line explanation}

| File / Module | Impact Type | Description | Owner |
|---------------|-------------|-------------|-------|
| {path} | {API contract / Shared package / Config / Pipeline / Data contract / Transitive} | {what specifically is affected} | {person from SME discovery} |

{repeat for each impacted repo, ordered by risk level descending}

## Dependency Chain

```mermaid
graph LR
  ChangedRepo["{changed repo}"]
  ConsumerA["{consumer A}"]
  ConsumerB["{consumer B}"]
  TransitiveC["{transitive C}"]

  ChangedRepo -->|{mechanism}| ConsumerA
  ChangedRepo -->|{mechanism}| ConsumerB
  ConsumerA -->|{mechanism}| TransitiveC
```

## Risk Assessment

| Repo | Risk | API Breakage | Coverage Gap | Staleness | Complexity | Depth |
|------|------|-------------|-------------|-----------|------------|-------|
| {repo} | {level} | {H/M/L} | {H/M/L} | {H/M/L} | {H/M/L} | {direct/transitive} |

### Key Risk Factors
1. {highest risk factor with explanation}
2. {second risk factor}
3. {third risk factor}

## Recommended Implementation Plan

| Order | Repo | Action | Reason | Owner |
|-------|------|--------|--------|-------|
| 1 | {most upstream} | {what to change} | {all consumers depend on this} | {person} |
| 2 | {consumer A} | {what to change} | {can proceed after step 1} | {person} |
| 2 | {consumer B} | {what to change} | {can proceed in parallel with 2} | {person} |
| 3 | {transitive} | {what to change} | {depends on step 2} | {person} |

## SME Contacts

| Area | Person | Role | Discovery Source |
|------|--------|------|-----------------|
| {component} | {name} | {role/title} | {WorkIQ design doc / ADO PR author / ADO work item} |

## Design Context

{Summary of relevant design documents and decision history found via WorkIQ, explaining WHY the current architecture exists and any constraints on the proposed change}

## Related Resources

| Resource | Type | Link |
|----------|------|------|
| {design doc} | SharePoint | {link} |
| {ADO wiki} | Wiki | {link} |
| {MS Learn guide} | Documentation | {link} |
```

## NEXT STEPS

After generating the report, suggest these follow-up actions:
- Run `/Octane.SDLCToolkit.ImpactQuickScan` for a lightweight scan of additional repos
- Use `/Octane.SDLCToolkit.OnboardingBuddy` to ask follow-up questions about any impacted repo
- Run `/Octane.SDLCToolkit.DesignReview` on the design doc if one exists for this change
- Run `/Octane.SDLCToolkit.OracleAnalyze` on the changed files for regression risk within each repo

## Variables

- `{change_description}` (string, required): Natural language description of the proposed change
- `{ado_org}` (string, required): Azure DevOps organization (from environment or user input)
- `{ado_project}` (string, optional): Azure DevOps project — omit to search all projects
