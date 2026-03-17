---
agent: AINativeDaemon
description: Trigger a governed code review on a specific pull request
tools: ['execute']
---

# Review a Pull Request

Run the full code review pipeline on a PR: code quality, security, test coverage, and patterns.

## Inputs

- repo (string, required) — Repository in owner/repo format (GitHub) or org/project/repo format (ADO)
- number (number, required) — Pull request number to review

## Instructions

1. **Identify the PR** from user context (number and repo).

2. **Run the review**:
   ```bash
   python -m daemon run --agent code-reviewer --repo ${input:repo} --pr ${input:number}
   ```

3. **Present findings** grouped by severity:
   - 🔴 Critical (must fix before merge)
   - 🟡 Warning (should fix)
   - 🟢 Nit (optional improvement)

4. **If daemon is running**, the review will auto-post to the PR.

## Expected Output

Code review findings grouped by severity (critical, warning, nit), including file locations, descriptions, and suggested fixes.

## Example User Prompts

- "Review PR #42"
- "Check the latest PR on my-org/my-repo"
- "Run code review and security scan on this PR"
