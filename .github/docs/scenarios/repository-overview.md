# Repository Overview Scenario

## Overview

The Repository Overview scenario provides a comprehensive workflow for analyzing and documenting codebases. It uses the Planner agent to generate high-level repository documentation, architecture analysis, and development insights.

## What's Included

### Agents
- **Planner** (shared) - Orchestrates repository analysis and documentation generation

### Prompts
- **Octane.Planner.RepositoryOverview** - Generates comprehensive repository documentation

### MCP Servers Required
- **code-search** - For analyzing codebase structure and searching code

## Prerequisites

- Azure DevOps repository configured
- Code Search MCP server configured with organization, project, and repository details

## Example Workflows

### Generate Repository Documentation

1. Open GitHub Copilot Chat
2. Type: `/Octane.Planner.RepositoryOverview`
3. The Planner will analyze your repository and generate:
   - Project overview and purpose
   - Architecture and design patterns
   - Key components and their responsibilities
   - Development setup instructions
   - Technology stack analysis

## Use Cases

- Onboarding new team members
- Creating project documentation
- Understanding unfamiliar codebases
- Generating architecture diagrams and explanations
- Documenting technical decisions

## Difficulty

**Beginner** - Simple to use, requires minimal setup

## Tags

`documentation` `planning` `repository` `overview`
