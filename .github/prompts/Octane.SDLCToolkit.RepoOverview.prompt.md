---
agent: SdlcSpecialist
description: Generate a comprehensive repository onboarding guide grounded in code, enterprise knowledge, and official docs.
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo', 'code-search/*', 'work-iq/*', 'ado/*', 'ms-learn/*']
model: Claude Opus 4.6 (copilot)
---

## PRIMARY DIRECTIVE

Generate a **comprehensive repository onboarding guide** for the repository. This is not just a code-level overview — it combines codebase analysis with enterprise knowledge (design reviews, requirements, decision history, operational context, and ownership) to produce a single-source-of-truth document that enables new engineers to reach "productive first PR" as quickly as possible.

## Inputs

- `overview_path` (string, optional): Output file path. Defaults to `${config.project.overview_path}` or `./docs/onboarding_guide.md`

The document must be:
- **Comprehensive** — grounded in code AND adjacent engineering artifacts (design docs, tech specs, Teams discussions, meeting notes, ADO work items)
- **Machine-readable** and structured for autonomous execution by AI systems or human teams
- **Deterministic**, with no ambiguity or placeholder content
- **Actionable** — includes ownership info, key contacts, and links to living documents

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Deep Codebase Research**
   - Use the #runSubagent tool to invoke a sub-agent that will:
     - Use the `code-search/*` tools to examine the overall repository structure — key directories, modules, and components
     - Analyze dependency files (package.json, requirements.txt, .csproj, etc.) to extract exact versions and purposes
     - Identify the high-level solution architecture, component interactions, and data flow
     - Perform deep analysis of the technical stack — languages, frameworks, libraries, design patterns, and infrastructure services
     - Identify and describe the key modules, their purpose, main functionalities, and how they fit into the overall architecture
     - Respond with a structured summary of repository structure, technical stack, architecture, and key modules

2. **Enterprise Knowledge Research**
   - Use the #runSubagent tool to invoke three parallel sub-agents:
     - **Design & Requirements sub-agent**: Use the `work-iq/*` tools to search for design reviews, tech specs, PRDs, and requirements documents related to this repository. Search SharePoint, OneDrive, and Teams for documents mentioning the repository name, key service names, or component names discovered in Step 1. Extract key decisions, rationale, and requirements.
     - **Decision History sub-agent**: Use the `work-iq/*` tools to search meeting notes, email threads, and Teams conversations for architectural decisions, trade-off discussions, and "why we chose X over Y" context. Focus on the last 12 months of history. Also use the `ado/*` tools to search ADO wiki pages and work item discussions for decision context.
     - **People & Ownership sub-agent**: Use the `work-iq/*` tools to identify subject matter experts — who authored key design docs, who participates in architecture discussions, who owns which components. Build an ownership map. Also use the `ado/*` tools to identify recent PR authors and work item assignees.

3. **Application Components, Data Architecture & API Specifications**
   - Use the #runSubagent tool to invoke three parallel sub-agents:
     - **Application Components sub-agent**: Use the `code-search/*` tools to identify and describe the major business and system components within the application — their namespaces, key classes/interfaces, and how they interact to fulfill requirements
     - **Data Architecture sub-agent**: Use the `code-search/*` tools to analyze data architecture — storage mechanisms, data models, access patterns, relationships, and any data flow diagrams
     - **API Specifications sub-agent**: Use the `code-search/*` tools to document API specifications if the repository exposes any APIs — endpoints, request/response formats, authentication mechanisms, and integration points with other systems

4. **Operational & Official Documentation Context**
   - Use the #runSubagent tool to invoke two parallel sub-agents:
     - **Operational Context sub-agent**: Use the `ado/*` tools to find operational runbooks, incident history, deployment procedures, and SLA/SLO information in ADO wikis and work items. Use the `work-iq/*` tools to search for operational reviews, S360 reports, and post-incident analysis.
     - **Official Documentation sub-agent**: Use the `ms-learn/*` tools to find official Microsoft documentation for Azure services, SDKs, and frameworks used by this repository. Link to relevant guides, best practices, and migration docs.

5. **Draft the Onboarding Guide**
   - Synthesize findings from ALL sub-agents into a complete onboarding guide
   - Follow the mandatory template format with all required sections
   - Include mermaid diagrams where appropriate to illustrate architecture
   - Ensure all content is based on actual analysis, not placeholders
   - Include links to source documents discovered via WorkIQ
   - Include the ownership/people map

6. **Review and Refine**
   - Use the #runSubagent tool to invoke a sub-agent that will:
     - Critically review the drafted document for completeness, accuracy, and clarity
     - Use the `code-search/*` tools to verify key claims against the actual codebase
     - Validate that no major modules, components, or patterns were missed
     - Check for gaps in logic or missing information
     - Verify that enterprise context (decisions, requirements, ownership) is properly represented
     - Make necessary adjustments based on self-review

## FILE NAMING CONVENTION

- File Location: `${config.project.overview_path}`, which defaults to `./docs/onboarding_guide.md`

## MANDATORY TEMPLATE

```markdown
---
repository: [Repository Name]
version: [e.g., 1.0.0]
date_created: [YYYY-MM-DD]
last_updated: [YYYY-MM-DD]
owner: [Team/Individual responsible]
type: [e.g., Microservice, Monolith, Library, Full-Stack Application]
---

# Onboarding Guide

[Brief description of the repository's purpose, the problem it solves, and its role in the larger ecosystem. This should be understandable by a new hire on day one.]

## 1. Quick Start

**Goal:** Get a new developer from clone to running the service locally.

### Prerequisites
[List exact tools, versions, and access requirements]

### Setup Steps
[Numbered steps to build, configure, and run the service]

### First Good Issues
[Link to ADO queries or labels for starter tasks]

## 2. Why This Service Exists

**Summary**: [Business context — what problem does this solve and for whom?]

### Requirements & Design History
[Key requirements documents, design reviews, and tech specs discovered via WorkIQ. Include links to source documents.]

### Key Decisions

[Architecture decisions and their rationale — extracted from design docs, meeting notes, and email threads]

| Decision | Rationale | Date | Source |
|----------|-----------|------|--------|
| [e.g., "Chose CosmosDB over SQL"] | [Why] | [When] | [Link to design doc/meeting] |

## 3. Technical Stack

**Summary**: [High-level overview of the technology choices and rationale]

### Core Technologies

| Category | Technology | Version | Purpose |
|----------|------------|---------|---------|
| [Fill from codebase analysis] | | | |

### Infrastructure Services

| Service | Provider | Purpose | Configuration |
|---------|----------|---------|---------------|
| [Fill from codebase analysis] | | | |

### Official Documentation References
[Links to Microsoft Learn guides for the Azure services and frameworks used — discovered via ms-learn MCP]

## 4. Solution Architecture

**Summary**: [Describe the architectural pattern and key design decisions]

### System Components
[Mermaid diagram showing component relationships]

### Architectural Patterns

| Pattern | Implementation | Rationale |
|---------|---------------|-----------|
| [Fill from codebase analysis] | | |

## 5. Project Structure

**Summary**: [Overview of the repository organization and module structure]

### Directory Layout
[Tree view of key directories with descriptions]

### Key Modules

| Module | Path | Responsibility | Dependencies |
|--------|------|----------------|--------------|
| [Fill from codebase analysis] | | | |

## 6. Application Components

**Summary**: [Description of the major functional components]

### Business Components

| Component | Namespace | Description | Key Classes |
|-----------|-----------|-------------|-------------|
| [Fill from codebase analysis] | | | |

## 7. Data Architecture

**Summary**: [Overview of data storage, models, and access patterns]

### Data Models

| Entity | Table/Collection | Description | Relationships |
|--------|-----------------|-------------|---------------|
| [Fill from codebase analysis] | | | |

## 8. API Specifications

**Summary**: [Overview of API design and endpoints]

### API Endpoints

| Endpoint | Method | Purpose | Authentication |
|----------|--------|---------|----------------|
| [Fill from codebase analysis] | | | |

### Integration Points

| System | Protocol | Direction | Purpose |
|--------|----------|-----------|---------|
| [Fill from codebase analysis] | | | |

## 9. Ownership & Contacts

**Summary**: [Who to ask when you have questions]

| Area | Owner | Role | Contact |
|------|-------|------|---------|
| [Fill from WorkIQ people discovery + ADO PR/work item analysis] | | | |

### Recent Contributors
[Top contributors from recent PRs and work items — auto-discovered from ADO]

## 10. Operational Context

**Summary**: [How this service runs in production]

### Deployment
[Deployment pipeline, environments, and procedures — from ADO wikis and operational docs]

### Monitoring & Alerts
[Key dashboards, alert rules, and on-call info]

### Known Issues & Gotchas
[Common pitfalls, known technical debt, and things that surprise new developers — from incident history and team discussions]

## 11. Related Resources

| Resource | Type | Link |
|----------|------|------|
| [Design docs, tech specs, runbooks discovered via WorkIQ] | | |
| [ADO wiki pages] | | |
| [Microsoft Learn guides] | | |

---

*Last Updated: [Date]*

```

## NEXT STEPS

After generating the guide, suggest these follow-up actions:
- Run `/Octane.SDLCToolkit.OnboardingPR` to create a PR for SME review
- Use `/Octane.SDLCToolkit.OnboardingBuddy` for interactive follow-up questions
