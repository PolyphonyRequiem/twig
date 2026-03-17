---
agent: AINativeDaemon
description: Draft a technical specification from a GitHub issue or ADO work item description
tools: ['execute', 'web']
---

# Draft Technical Specification

Generate a structured technical spec from an issue or work item, covering architecture, implementation, testing, and rollout.

## Inputs

- repo (string, required) — Repository in owner/repo format (GitHub) or org/project/repo format (ADO)
- number (number, required) — Issue number (GitHub) or work item ID (ADO)

## Instructions

1. **Run the spec drafter**:
   ```bash
   python -m daemon run --agent spec-drafter --repo ${input:repo} --issue ${input:number}
   ```

2. **Spec sections**: Overview, Architecture, Implementation Steps, Testing Strategy, Rollout Plan

3. **Flag items needing human decision** with ⚠️ markers.

## Expected Output

A structured technical specification with Overview, Architecture, Implementation Steps, Testing Strategy, and Rollout Plan sections, with ⚠️ markers for items needing human decisions.

## Example User Prompts

- "Draft a spec for issue #14"
- "Write a technical specification for the auth migration"
- "Generate an ADR for this proposal"
