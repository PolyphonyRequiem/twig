# Spec-Driven Development (SDD) Scenario

## Overview

The Spec-Driven Development scenario implements a complete Requirements (optional) → PRD → Implementation workflow. It guides teams through structured software development using requirements documents, product specifications, and code reviews.

## What's Included

### Agents
- **Planner** (shared) - Creates requirements and specifications
- **Coder** - Implements features and reviews code

### Prompts
- **Octane.Planner.Requirements** - Generate requirements documents
- **Octane.Planner.Plan** - Create product requirement documents (PRDs)
- **Octane.Coder.Implement** - Implement features from PRD specifications
- **Octane.Coder.Review** - Review code changes against requirements

### Instructions
- Requirements document structure and guidelines
- PRD template and best practices

### Templates
- Requirements document template
- PRD template
- Code review template

### MCP Servers Required
- **code-search** - For code analysis and implementation

## Prerequisites

- Azure DevOps repository configured
- Understanding of requirements-driven development
- Familiarity with PRD and requirements documentation

## Example Workflows

### Full SDD Workflow

1. **Create Requirements** (`@Octane.Planner.Requirements`)
   - Define user stories and acceptance criteria
   - Document functional and non-functional requirements

2. **Generate PRD** (`@Octane.Planner.Plan`)
   - Create detailed product specification
   - Include architecture and design decisions
   - Define Implementation Plan with epics and items

3. **Implement Features** (`@Octane.Coder.Implement`)
   - Implement epics/items from the PRD
   - Follow PRD specifications

4. **Review Changes** (`@Octane.Coder.Review`)
   - Validate implementation against requirements
   - Generate review reports

## Use Cases

- Feature development with clear requirements
- Team collaboration with structured specs
- Ensuring implementation matches specifications
- Maintaining documentation throughout development
- Code review automation

## Difficulty

**Intermediate** - Requires understanding of software development lifecycle and specification documents

## Tags

`development` `planning` `implementation` `requirements` `spec`

## Related Scenarios

### Research-First Development

If your work item is complex, ambiguous, or involves multiple stakeholders, consider using **Research-First Development** before SDD. It gathers context from ADO, M365, EngHub, and the codebase to ensure you fully understand the problem before writing specs.

| Scenario | Best For |
|----------|----------|
| **Spec-Driven Development** | Clear requirements, well-scoped features |
| **Research-First Development** | Complex/ambiguous work items, stakeholder alignment needed |
| **Both (full-dev-workflow preset)** | End-to-end workflow from ambiguous ticket to merged PR |

**Install both**: `octane install --preset full-dev-workflow`
