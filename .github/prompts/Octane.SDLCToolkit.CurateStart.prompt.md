---
agent: SdlcSpecialist
description: Analyze repository for documentation gaps and prioritize artifacts by Copilot impact
tools: ['runCommands', 'search', 'githubRepo']
---

# Instructions

Get started with documentation curation by analyzing your repository to discover existing artifacts and identify gaps that would improve GitHub Copilot's understanding.

## Inputs

- `path` (string, optional): Path to the repository root to analyze. Defaults to `.`

## Why Start Here?

This analysis gives you a complete picture of your documentation state:
- What artifacts already exist
- What's missing and would help Copilot most
- Prioritized recommendations for what to create first

## Process

1. **Run Analysis**: Execute the sdlc-toolkit curate command to scan the repository
2. **Interpret Results**: Explain what artifacts exist and what's missing
3. **Prioritize Gaps**: Rank missing artifacts by Copilot impact
4. **Recommend Actions**: Suggest specific artifacts to create first

## Analysis Command

```bash
sdlc-toolkit curate .
```

## Expected Output

Provide a summary including:
- **Existing Artifacts**: What documentation already exists
- **High Priority Gaps**: Missing artifacts with significant Copilot impact
- **Medium Priority Gaps**: Nice-to-have documentation
- **Recommendations**: Specific next steps

## Copilot Impact Levels

| Artifact Type | Impact | Why |
|--------------|--------|-----|
| context-doc | 🔴 High | Loaded automatically, sets project context |
| glossary | 🔴 High | Improves naming and terminology |
| adr | 🔴 High | Explains architectural decisions |
| coding-standard | 🟡 Medium | Ensures consistent code style |
| api-spec | 🟡 Medium | Correct API usage |
| design-doc | 🟢 Lower | Feature-specific context |

## After Analysis

Offer to help create the highest-priority missing artifact. Common next steps:
- "Create a context document" → Sets up `.github/copilot-instructions.md`
- "Create an ADR for [decision]" → Documents architectural choices
- "Build a glossary" → Defines domain terminology

---

# Task

Analyze this repository for documentation artifacts and identify what's missing for optimal GitHub Copilot assistance.
