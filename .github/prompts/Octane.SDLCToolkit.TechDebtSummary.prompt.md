---
agent: SdlcSpecialist
description: Quick summary of technical debt
tools: ['runCommands']
---

# Technical Debt Summary

Get a quick overview of technical debt without full details.

## Instructions

When the user asks for a quick debt summary:

1. **Run summary command**:
   ```bash
   sdlc-toolkit techdebt-summary .
   ```

2. **Present the summary** clearly with:
   - Total item count
   - High severity count
   - Breakdown by category

3. **Offer to dive deeper** if numbers are concerning

## Example User Prompts

- "Give me a quick debt summary"
- "How much tech debt do we have?"
- "Quick health check"
