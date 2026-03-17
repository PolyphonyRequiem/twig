---
agent: Planner
description: Generate a REQ document that contains detailed requirements for a specified feature or change.
tools: ['vscode', 'execute', 'read', 'agent', 'edit', 'search', 'web', 'todo', 'code-search/*']
model: Claude Opus 4.6 (copilot)
---

## INPUTS

- `Purpose` (string, required): A clear and concise description of the feature, change, or initiative that the requirements should address.

If you do not have a `Purpose`, you cannot proceed. You must stop and ask for this input. Do not provide an example request. Just specify that the input is required.

## PRIMARY DIRECTIVE

Generate a **Requirements Document** for the initiative described in `${input:Purpose}`. The requirements must be:
- **Compliant** with all template format, structure, and guidelines
- **Machine-readable** and structured for autonomous execution by AI systems or human teams
- **Deterministic**, with no ambiguity or placeholder content

## WORKFLOW STEPS

Present the following steps as **trackable todos** to guide progress:

1. **Understand the Request**  
   - Thoroughly review the `${input:Purpose}` document or instructions.
   - Identify and note all key features, requirements, and constraints outlined in the purpose.

2. **High-Level Review of Codebase**  
   - Use the #runSubagent tool to invoke a sub-agent that will:
      - Read the `${config:project.overview_path}` or README.md to understand the system architecture and relevant modules.
      - Perform a high-level review of the codebase using the `code-search/*` tools to identify existing functionality related to the requested change.
      - Identify the key components, services, or modules that will be affected by the proposed change.
      - Respond to the main agent with all relevant context for drafting the requirements.

3. **Deep Code Analysis**  
   - Use the #runSubagent tool to invoke a sub-agent that will:
      - Analyze the identified components, services, or modules in detail using the `code-search/*` tools.
      - Identify all related components and dependencies within the codebase that may impact the new requirements.
      - Gather insights and details on the existing implemenation and all related code.
      - Respond to the main agent with all relevant context for drafting the requirements.

4. **Synthesize Information**
   - Combine insights from the `${input:Purpose}`, high-level review, and deep code analysis.
   - Create a comprehensive understanding of the feature or change, including scope, constraints, and dependencies.

5. **Draft the Requirements Document**
    Complete the requirements document with all required sections, tables, metadata, and guidance from the `requirements.instructions.md` file.

6. **Review and Refine**
  - Use the #runSubagent tool to invoke a sub-agent that will:
    - Critically review the drafted requirements document.
    - Ensure the document is comprehensive, clear, and actionable.
    - Validate that all aspects of the purpose have been addressed.
    - Make necessary adjustments based on self-review.

## FILE NAMING CONVENTION

- Save the requirements document under `${config:planner.artifacts.requirements}`, which defaults to `docs/projects/`
- Use the following naming convention: `[purpose]-[component]/[purpose]-[component].req.md`
- Examples:
  - `upgrade-database-v2/upgrade-database-v2.req.md`
  - `add-user-authentication/add-user-authentication.req.md`

## NEXT STEPS

After completing the requirements document, present the following next steps to the user:

**Your requirements document has been created. Here are your next steps:**

1. **Review the requirements** - Validate the document with stakeholders
2. **Generate a PRD** - Create a detailed implementation plan:
   ```
   /Octane.Planner.Plan <path-to-your-req.md>
   ```

