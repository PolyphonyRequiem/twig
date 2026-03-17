# Living Documentation Scenario

**Generate and maintain living documentation for existing codebases.**

## Overview

The Living Documentation scenario provides AI-assisted workflows for documenting existing systems. Unlike Spec-Driven Development (which plans before building), this scenario helps teams document code that already exists—capturing design decisions, team knowledge, and architecture that would otherwise be lost.

**SDD answers:** "How do we build this?"  
**Living Docs answers:** "Why did we build it this way?"

### Knowledge Sources

The Living Documentation scenario now leverages enterprise knowledge via WorkIQ to enrich documentation with context that cannot be found in code alone:

| Source | MCP Server | What It Provides |
|--------|-----------|------------------|
| Code, repos | `code-search` | Architecture, components, dependencies |
| SharePoint, Teams, emails, meetings | `work-iq` | Design decisions, rationale, discussion context |
| Official Microsoft docs | `ms-learn` | Azure service guides, best practices |

## When to Use

- **Legacy systems** - Code exists but documentation is missing or stale
- **After SDD completion** - Document what was built for future maintainers
- **Knowledge capture** - Before team members leave or rotate
- **Onboarding preparation** - Create docs that help new developers ramp up
- **Architecture reviews** - Generate baseline documentation for review

## Prerequisites

- **Code Search MCP server** — configured with your ADO organization, project, and repository
- **MS Learn MCP server** — for official Microsoft documentation references
- **WorkIQ MCP server** (optional) — for enterprise knowledge from SharePoint, Teams, emails, and meetings. Requires tenant admin consent on first use — see [WorkIQ docs](https://github.com/microsoft/work-iq-mcp). If unavailable, documentation will be generated from code analysis and MS Learn only.

## Quick Start

```shell
# 1. Generate docs
/Octane.Doc.Create Service.Authentication

# 2. AI creates docs/Service.Authentication.doc.md (path configurable)
# 3. AI asks: "Would you like to add team insights?"
# 4. Say yes → Answer 5-7 questions about design decisions, gotchas, production learnings
# 5. Review generated doc and commit
```

**That's it.** The AI handles template structure, formatting, and section organization.

## Slash Commands

| Command | Purpose | Output |
|---------|---------|--------|
| `/Octane.Doc.Create <name>` | Generate docs for existing code | `docs/<name>.doc.md` |
| `/Octane.Doc.Update <name>` | Add insights to existing docs | Updated doc |
| `/Octane.Doc.Insights` | Guided Q&A to capture team knowledge | Insights added to docs |

## Workflow

### Generate New Documentation

```shell
# Generate comprehensive docs for a service
/Octane.Doc.Create Service.Authentication

# AI analyzes codebase and generates structured documentation
# Output: docs/Service.Authentication.doc.md (path configurable)
```

### Update Existing Documentation

```shell
# Add a new insight or gotcha
/Octane.Doc.Update Service.Authentication add insight: Redis connections timeout after 30s idle

# Add a design decision
/Octane.Doc.Update Service.Authentication add decision: Why JWT over session cookies
```

### Capture Team Knowledge

```shell
# Start an interview session to capture tribal knowledge
/Octane.Doc.Insights Service.Authentication

# AI asks targeted questions:
# - "What was the hardest bug to debug?"
# - "What would you change if starting over?"
# - "What do new developers always get wrong?"
```

## Document Structure

Generated documents follow a consistent structure:

```markdown
# [Name]

## Overview
- What: Brief description
- Why: Problem it solves
- When: Usage scenarios

## Core Architecture
- Flow diagram (Mermaid)
- Key classes/modules
- Design decisions (with rationale)

## API Reference
- Interfaces
- Configuration
- Dependencies

## Common Scenarios
- How-to guides
- Troubleshooting

## Key Insights
- Performance characteristics
- Gotchas and edge cases
- Production learnings

## Testing
- Coverage summary
- Test patterns
```

## Integration with SDD

Living Documentation complements Spec-Driven Development:

```shell
# Phase 1: Plan (SDD)
/Octane.Planner.Requirements migrate to redis
/Octane.Planner.Plan for redis-migration.req.md

# Phase 2: Build (SDD)
/Octane.Coder.Implement EPIC-001

# Phase 3: Document (Living Docs)
/Octane.Doc.Create RedisCacheService
/Octane.Doc.Insights  # Capture what you learned
```

## Configuration

Configure output paths in `.config/octane.yaml`:

```yaml
documentation:
  output_path: ./docs/              # default, override per project
  template: documentation.template.md
  include_diagrams: true
  diagram_syntax: azure-devops      # or github
```

## Best Practices

1. **Document early, update often** - Don't wait until code is "done"
2. **Capture the "why"** - Code shows "what", docs should explain "why"
3. **Use interviews** - Team knowledge is the most valuable and most perishable
4. **Keep pseudo-code** - Real code drifts, pseudo-code captures intent
5. **Review quarterly** - Set reminders to validate docs against implementation

## Related Scenarios

- **Repository Overview** - High-level repo summary (good starting point)
- **Spec-Driven Development** - Plan and build new features
- **Test Analysis** - Understand test coverage
