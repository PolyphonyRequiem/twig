---
agent: Coder
description: Implement a feature based on an epic or item from a PRD.
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo', 'code-search/*']
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `PRD` (string, required): A link to a PRD file (e.g. `.prd.md`) that contains the Implementation Plan with epics and items.
- `Epic` (string, optional): The specific epic within the PRD that contains the item you will be implementing.

If you do not have a `PRD`, you cannot proceed. You must stop and ask for this input. Do not provide an example request. Just specify that the input is required.

## PRIMARY DIRECTIVE

Your goal is to implement features from the `${input:PRD}` document. You will focus on the specific epic identified by `${input:Epic}`. If no epic is provided, you will consider all epics to be in scope.

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **PRD Review**  
  - Review in detail the provided `${input:PRD}` document. Epics that are marked as DONE are out of scope.
  - Review the `Files Affected` section to identify which files will be created, modified, or deleted.

2. **Implementation**
  - For each epic that is in scope, use the #runSubagent tool to invoke a sub-agent that will:
    - Thoroughly review the epic details.
    - Use the `code-search/*` tools perform deep code analysis to identify all relevant code, identify dependencies, and understand coding patterns.
    - Create a logical plan for implementing the epic.
    - Make the necessary code changes.
    - Fix any errors, bugs, or issues that arise during implementation.
    - Ensure all changes adhere to best practices and coding standards.
    - Update the `${input:PRD}` document to reflect the completion of the epic.
    - Create a git commit with a clear and descriptive message summarizing the changes made for the epic. The commit message should start with the epic ID (e.g., "EPIC-001: Implement feature X").

3. **Review**  
  - Use the #runSubagent tool to invoke a sub-agent that will:
    - Conduct a thorough review of all code changes made.
    - Use the `code-search/*` tools perform deep code analysis to identify all updated and impacted code.
    - Ensure that the implementation
      - Meets all requirements outlined in the PRD 
      - Is free of bugs and errors
      - Includes appropriate tests and documentation
      - Strictly follows best practices and coding standards
    - If any issues are found, create a detailed list of required fixes and improvements, then return to the main agent.

## NEXT STEPS

After completing the implementation, present the following next steps to the user:

**Implementation complete. Here are your next steps:**

1. **Implement the next epic** (if more epics remain in the PRD):
   ```
   /Octane.Coder.Implement <path-to-your-prd.md> EPIC-00X
   ```

2. **Review all changes** - Validate implementation against the PRD:
   ```
   /Octane.Coder.Review <path-to-your-prd.md> <commit-range-or-branch-or-stagedOrUnstagedFiles>
   ```

3. **Commit your changes** - Stage and commit the implementation