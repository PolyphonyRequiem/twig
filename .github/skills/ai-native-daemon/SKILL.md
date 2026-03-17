---
name: ai-native-daemon
description: >
  Governed agent fleet for software engineering — code review, security scanning,
  test generation, spec drafting, release notes, and full SDLC pipeline automation
  with enterprise governance, trust scoring, and human-in-the-loop approval.
  Automatically activated when users request: daemon management ("start watching",
  "monitor repos"), code review ("review PR"), security scanning ("security scan"),
  test generation ("generate tests"), or spec drafting ("draft spec").
---

# AI Native Daemon Skill

## Overview

The AI Native Daemon is a production-grade fleet of 11 AI agents that continuously
watches GitHub and Azure DevOps repositories, triages issues/work items, reviews PRs,
generates tests, and manages releases with enterprise governance.

## Capabilities

1. **Code Review** — Correctness, security, performance, pattern compliance
2. **Security Scanning** — OWASP Top 10, secrets, CVEs, IaC misconfig
3. **Test Generation** — Framework-aware scaffolding for untested code
4. **Spec Drafting** — Technical specifications from issue/work item descriptions
5. **Release Notes** — Categorized changelogs from merged PRs
6. **Repo Health** — 5-dimension health scoring
7. **IaC Validation** — Bicep, Terraform, ARM, GitHub Actions
8. **Refactor Advisory** — Complexity, duplication, dead code detection
9. **Fleet Orchestration** — Multi-agent pipeline coordination
10. **Proactive Research** — Daily automated improvement discovery
11. **Implementation** — Addresses review feedback and implements changes

## Platform Support

| Feature | GitHub | Azure DevOps |
|---------|--------|-------------|
| Poll PRs | ✅ | ✅ |
| Poll Issues/Work Items | ✅ | ✅ (WIQL) |
| Post Comments | ✅ | ✅ (threads) |
| Merge PRs | ✅ | ✅ |
| Labels/Tags | ✅ | ✅ |
| Commit Polling | ✅ | ✅ |
| Webhook Events | via GitHub Events | via Service Hooks |
| Custom Instructions | ✅ .ai-native/ | ✅ .ai-native/ |
| Tech Stack Detection | ✅ Languages API | ✅ File extension scan |

## Governance

Every execution passes through: Trust Gate → Circuit Breaker → Token Budget → Agent → Output Check → Metrics

## Error Handling

- **Daemon not running**: Suggest starting with `python -m daemon watch`
- **Agent circuit open**: Report which agent failed and suggest waiting for auto-heal
- **Rate limited**: Report partial results and suggest retry
- **Repo not accessible**: Check `gh auth status` and repo permissions

## References

- [Setup Guide](references/setup.md)
- [Agent SRE Setup Guide](references/agent-sre-setup.md)
