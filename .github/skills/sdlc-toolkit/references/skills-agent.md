---
name: sdlc-toolkit
description: CLI for SDLC agent capabilities. Use when running tech debt scans, design reviews, safety audits, artifact curation, regression analysis, or any code quality task.
---

# Skills Agent

CLI tool for running SDLC agent capabilities including tech debt discovery, design review, safety audits, artifact curation, and regression prediction.

## Setup

**First time?** → See [references/setup.md](references/setup.md) for installing `sdlc-toolkit`.

Verify: `sdlc-toolkit --help`

## Quick Reference

```bash
# Tech Debt
sdlc-toolkit techdebt-summary .                    # Quick summary
sdlc-toolkit techdebt-hotspots .                   # Find hotspots
sdlc-toolkit techdebt-scan ./src                   # Full scan

# Design Review
sdlc-toolkit design-review design.md               # Full review
sdlc-toolkit design-rules                           # List rules
sdlc-toolkit design-rules --category security       # Filter by category

# Safety Audit
sdlc-toolkit safety-check my-agent --code ./src     # Comprehensive check
sdlc-toolkit injection-test my-agent                # Prompt injection test
sdlc-toolkit pii-scan ./src                         # PII detection
sdlc-toolkit sbom ./project                         # SBOM generation

# Artifact Curation
sdlc-toolkit curate .                               # Analyze gaps
sdlc-toolkit kb-index .                             # Index for search
sdlc-toolkit kb-search "authentication"             # Search knowledge

# Regression Oracle
sdlc-toolkit oracle-ingest bugs.json                # Ingest bug data
sdlc-toolkit oracle-analyze file1.py file2.py       # Analyze PR risk
```

## MCP Servers

This skill works best with these MCP servers enabled:

| Server | Purpose |
|--------|---------|
| code-search | Code navigation and search |
| ado | Azure DevOps work items and wikis |
| ms-learn | Microsoft documentation |
| work-iq | Enterprise knowledge (SharePoint, Teams, email) |

## References

- [Setup Guide](references/setup.md) - Installation instructions
- [Security Rules](references/security-rules.md) - Auth, encryption, input validation
- [Reliability Rules](references/reliability-rules.md) - Retries, timeouts, degradation
- [Architecture Rules](references/architecture-rules.md) - Scalability, caching
- [Attack Patterns](references/attack-patterns.md) - Prompt injection categories
- [PII Patterns](references/pii-patterns.md) - Sensitive data detection
