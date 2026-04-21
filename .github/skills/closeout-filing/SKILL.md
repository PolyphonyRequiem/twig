---
name: closeout-filing
description: Automatically file ADO work items from conductor SDLC closeout observations. Takes a completed epic's closeout JSON (observations + improvements) and creates Issues and Tasks under the Closeout Findings epic, with deduplication against existing items. Use after a conductor SDLC run completes, or when you have closeout observations to file.
user-invokable: true
---

# Closeout Findings Filing

Automates the pattern of taking SDLC workflow closeout observations and filing them as
actionable ADO work items. Deduplicates against existing items to avoid double-filing.

> **This workflow is lightweight** — typically 5-10 minutes. Use `conductor run ... --web`
> for a real-time dashboard. Load `.github/skills/conductor/SKILL.md` for execution details.

## Prerequisites

- `conductor` skill — load `.github/skills/conductor/SKILL.md` for installation
- `twig` conductor registry — `conductor registry add twig --source github://PolyphonyRequiem/twig-conductor-workflows`
- `twig` CLI — configured with an ADO workspace
- A completed SDLC run with closeout observations (the JSON blob from `close_out` agent)

## Workflow

| Workflow | Purpose | Key Inputs |
|----------|---------|------------|
| `closeout-filing@twig` | Scan existing items, dedup, file new Issues/Tasks | `epic_id`, `observations`, `improvements` |

## Quick Reference

```bash
# File findings from a closeout JSON blob
conductor run closeout-filing@twig --web \
  --input epic_id=1519 \
  --input observations="Feature delivered in ~4 hours..." \
  --input 'improvements=["Enforce PR-per-group discipline: ...", "Add twig flush command: ..."]'

# Skip dedup (force-file all improvements as new tasks)
conductor run closeout-filing@twig --web \
  --input epic_id=1519 \
  --input observations="..." \
  --input 'improvements=[...]' \
  --input skip_dedup=true
```

## Chaining After SDLC

After a `twig-sdlc` conductor run completes, it outputs `observations` and `improvements`.
Pass these directly to this workflow:

```bash
# 1. Run the SDLC workflow (long-running)
conductor run twig-sdlc-full@twig --web --input work_item_id=1611

# 2. When it completes, take the closeout JSON and file findings
conductor run closeout-filing@twig --web \
  --input epic_id=1611 \
  --input observations="<from closeout output>" \
  --input 'improvements=<from closeout output>'
```

## What It Does

### Phase 1: Scanner
- Reads the completed epic's details via `twig set <epic_id>`
- Reads the Closeout Findings tree (`twig set 1603`, `twig tree`)
- Checks if a closeout Issue already exists for this epic
- Cross-references each improvement against ALL existing tasks under #1603
- Produces a filing plan: which items are new, which are duplicates

### Phase 2: Filer
- Creates the closeout Issue under #1603 (if one doesn't already exist)
- Creates Tasks for each non-duplicate improvement
- Adds rich markdown descriptions to all new items
- Verifies the created items via `twig tree`

## Conventions

- **Parent Epic**: All closeout findings live under Epic #1603 ("Follow Up on Closeout Findings")
- **Issue naming**: `<Epic Title> (Epic #<id>) Closeout`
- **Task naming**: Short actionable title from the improvement
- **Deduplication**: Semantic — compares meaning, not exact text match
- **Descriptions**: Always markdown-formatted with context, rationale, and acceptance criteria

## When to Use

- After any `twig-sdlc` conductor run completes
- When you have closeout observations from a manual review
- When the user pastes a closeout JSON blob and asks to "file these findings"

## When NOT to Use

- For filing bugs or feature requests unrelated to closeout observations
- When the observations have already been filed (check #1603 tree first)
