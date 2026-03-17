---
agent: SdlcSpecialist
description: Analyze a PR or changed files for regression risk based on historical bug patterns
tools: ['runCommands', 'search', 'changes']
---
# Analyze PR for Regression Risk

Get started with regression analysis by analyzing a pull request or set of changed files for regression risk based on historical bug patterns.

## Why Start Here?

This analysis gives you immediate value from the Regression Oracle:
- Risk assessment for your code changes
- Related historical bugs to watch for
- Specific recommendations to reduce risk
- Suggested tests based on patterns

## Instructions

1. Identify the changed files from the PR
2. Run the Regression Oracle analysis:
   ```bash
   sdlc-toolkit oracle-analyze <files...> --title "<PR title>"
   ```
3. Interpret the results:
   - **Risk Level**: critical/high/medium/low
   - **Confidence**: Based on available historical data
   - **Related Bugs**: Historical bugs that affected similar code
   - **Recommendations**: Specific actions to reduce risk
   - **Suggested Tests**: Tests based on bug patterns

4. Provide a clear summary to the developer

## Example

For a PR changing `src/auth/session.py`:

```bash
sdlc-toolkit oracle-analyze src/auth/session.py --title "Fix session timeout"
```

## Output Format

Provide:
1. Risk assessment (emoji + level + score)
2. Key risk factors
3. Related historical bugs
4. Top 3 recommendations
5. Suggested tests to add

## Next Steps

After analyzing a PR, common follow-up actions:
- "Show me the bug patterns" → View detected patterns across codebase
- "Suggest tests for these files" → Get specific test recommendations
- "Generate a risk report" → Full codebase risk analysis

## Variables

- `{changed_files}` (string, required): Space-separated list of changed file paths to analyze
- `{pr_title}` (string, required): Title of the pull request for context
- `{pr_number}` (string, optional): PR number for linking to work items
