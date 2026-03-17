---
agent: SdlcSpecialist
description: Find and analyze technical debt including code markers, stale branches, outdated dependencies, and code churn hotspots
tools: ['runCommands', 'search', 'code-search/*']
---

# Find Technical Debt

Scan the repository for technical debt including code markers, stale branches, and outdated dependencies.

## Instructions

When the user asks to find technical debt, analyze code health, or show TODOs/FIXMEs:

1. **Run the tech debt scanner**:
   ```bash
   sdlc-toolkit techdebt-scan .
   ```

2. **Interpret the results** for the user:
   - Highlight high-severity items first
   - Group related issues together
   - Suggest which items to address first

3. **If asked about specific areas**:
   ```bash
   sdlc-toolkit techdebt-scan ./src/specific-path
   ```

4. **For quick overview**:
   ```bash
   sdlc-toolkit techdebt-summary .
   ```

5. **For hotspot analysis**:
   ```bash
   sdlc-toolkit techdebt-hotspots . --top 10
   ```

## Response Format

Present findings in this order:
1. Quick summary (total count, high severity count)
2. High severity items with context
3. Medium severity patterns
4. Recommendations for next steps

## Example User Prompts

- "Find all the tech debt in this repo"
- "Show me the TODOs and FIXMEs"
- "How healthy is our codebase?"
- "What should we fix first?"
- "Are there any outdated dependencies?"
