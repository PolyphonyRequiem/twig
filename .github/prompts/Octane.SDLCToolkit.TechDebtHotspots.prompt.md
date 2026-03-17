---
agent: SdlcSpecialist
description: Find files with the most technical debt
tools: ['runCommands']
---

# Technical Debt Hotspots

Identify files with the highest concentration of technical debt.

## Instructions

When the user asks about debt hotspots or where to focus cleanup:

1. **Run hotspots command**:
   ```bash
   sdlc-toolkit techdebt-hotspots . --top 10
   ```

2. **Analyze the results**:
   - Which files have the most markers?
   - Are there patterns (same component, same team)?
   - What types of debt dominate?

3. **Recommend focus areas** based on:
   - High severity concentration
   - Recently active files
   - Shared/core code

## Example User Prompts

- "Which files have the most debt?"
- "Where should we focus cleanup?"
- "Show me the worst files"
- "Find debt hotspots"
