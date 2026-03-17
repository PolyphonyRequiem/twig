# Research-First Development

> Deep research methodology for complex engineering tasks. Understand before you build.

## Overview

This scenario implements a structured approach to tackling complex engineering work items. Instead of jumping straight to implementation, it guides AI agents through systematic knowledge gathering across multiple data sources before proposing solutions.

## The Problem It Solves

Common failure modes in AI-assisted development:
- ❌ Jumping to code without understanding the system
- ❌ Missing implicit contracts with downstream consumers
- ❌ Changes made in one place get overwritten elsewhere
- ❌ Skipping stakeholder alignment mentioned in tickets
- ❌ Underestimating complexity of "simple" changes

## The Solution

A phased approach that spends 30% of effort on understanding:

```
┌─────────────────────────────────────────────────────────────┐
│  Phase 0: GATHER                                            │
│  ├── ADO: Ticket details, history, linked items             │
│  ├── WorkIQ: Emails, docs, Teams, meeting notes             │
│  ├── EngHub: TSGs, architecture docs, API specs             │
│  └── Codebase: Trace complete code paths                    │
├─────────────────────────────────────────────────────────────┤
│  Phase 1: SYNTHESIZE                                        │
│  ├── Problem statement                                      │
│  ├── System context & architecture                          │
│  ├── Stakeholder map                                        │
│  └── Risk assessment                                        │
├─────────────────────────────────────────────────────────────┤
│  Phase 2: DESIGN                                            │
│  ├── Multiple design options                                │
│  ├── Tradeoff analysis                                      │
│  └── Recommendation with justification                      │
├─────────────────────────────────────────────────────────────┤
│  Phase 3: PLAN                                              │
│  ├── Stakeholder alignment plan                             │
│  ├── Phased implementation                                  │
│  └── Testing & rollout strategy                             │
└─────────────────────────────────────────────────────────────┘
```

## When to Use This Scenario

### Research-First vs Spec-Driven Development

| Use Research-First When... | Use Spec-Driven Development When... |
|---------------------------|-------------------------------------|
| Work item is complex or ambiguous | Requirements are already clear |
| Multiple stakeholders are involved | You have (or can quickly write) a spec |
| Past implementation attempts failed | Scope is well-defined |
| Hidden dependencies are likely | Feature is isolated |
| Effort estimate is high (13+ days) | Effort estimate is low (1-5 days) |
| Ticket says "align with team X first" | No cross-team alignment needed |

### Use Both Together

For maximum effectiveness, Research-First can feed into Spec-Driven Development:

```
Research-First (Phases 0-3)     Spec-Driven Development
┌─────────────────────────┐     ┌─────────────────────────┐
│ Gather → Synthesize →   │     │ Requirements → PRD →    │
│ Design → Plan           │ ──► │ Implement → Review      │
└─────────────────────────┘     └─────────────────────────┘
```

**Quick Start**: Use the `full-dev-workflow` preset to install both:
```bash
octane install --preset full-dev-workflow
```

## Prerequisites

### Required MCP Servers

| Server | Purpose | Setup |
|--------|---------|-------|
| **ADO** | Azure DevOps work items, PRs, repos | `npx -y @azure-devops/mcp <org>` |
| **WorkIQ** | Microsoft 365 data (emails, docs, Teams) | `npx -y @microsoft/workiq mcp` |
| **EngHub** | Engineering Hub documentation | See [EngHub Setup](#enghub-mcp-setup) |
| **code-search** | Azure DevOps code search | Already configured |

### EngHub MCP Setup

The EngHub MCP server requires cloning the repository and running locally:

```bash
# 1. Clone the repository (requires Microsoft GitHub EMU access)
git clone https://github.com/azure-core/enghub-mcp-server-tools
cd enghub-mcp-server-tools

# 2. Install dependencies and build
npm install
npm run build

# 3. Set the environment variable (add to your shell profile)
# Windows (PowerShell)
$env:ENGHUB_MCP_PATH = "C:\path\to\enghub-mcp-server-tools"

# macOS/Linux
export ENGHUB_MCP_PATH="/path/to/enghub-mcp-server-tools"
```

### WorkIQ First-Time Setup

WorkIQ requires tenant administrator consent on first use:

```bash
# Accept the EULA (required once)
npx -y @microsoft/workiq accept-eula

# Test the connection
npx -y @microsoft/workiq ask -q "What meetings do I have today?"
```

> ⚠️ **Note**: If you're not a tenant admin, contact your administrator. See the [WorkIQ Admin Guide](https://github.com/microsoft/work-iq-mcp/blob/main/ADMIN-INSTRUCTIONS.md).

## Usage

### Quick Start

1. **Install the scenario** via Octane CLI or VS Code extension
2. **Open a work item** you need to implement
3. **Use the Gather prompt** with your ticket ID:
   ```
   @Researcher /gather TICKET_ID=12345678
   ```
4. **Follow the phased prompts** through synthesis, design, and planning

### Available Prompts

| Prompt | Purpose | When to Use |
|--------|---------|-------------|
| `Octane.Research.Gather` | Collect context from all sources | Starting a new work item |
| `Octane.Research.Synthesize` | Summarize findings, map stakeholders | After gathering is complete |
| `Octane.Research.Design` | Generate design options with tradeoffs | After synthesis |
| `Octane.Research.Plan` | Create implementation plan | After design selection |

### Example Workflow

```
You: @Researcher I need to implement work item 35791219

Researcher: I'll gather comprehensive context for this work item.
            Let me search across ADO, M365, EngHub, and the codebase...

[Phase 0: Gathering from multiple sources]
[Phase 1: Synthesizing findings]
[Phase 2: Proposing design options]
[Phase 3: Creating implementation plan]
```

## What Makes This Different

### vs. Standard Copilot
- **Standard**: Code completion, single-file context
- **Research-First**: Cross-system context (ADO + M365 + EngHub + code), stakeholder awareness

## Best Practices

1. **Don't skip phases** - Each phase builds on the previous
2. **Trust the process** - 30% research time saves 50% rework time
3. **Surface blockers early** - Alignment needs should be identified before coding
4. **Use the templates** - Structured outputs ensure nothing is missed

## Templates

This scenario includes templates for:
- `stakeholder-map.md` - Document who needs to align/approve
- `design-options.md` - Compare multiple approaches
- `implementation-plan.md` - Phased delivery plan

## Contributing

Found an issue or have an improvement? 
- Open an issue at [github.com/azure-core/octane](https://github.com/azure-core/octane)
- Or submit a PR with your changes

## License

MIT - See the main Octane repository for details.
