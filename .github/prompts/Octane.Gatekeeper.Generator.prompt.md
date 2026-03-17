---
description: Generates anti-pattern documents based on provided code snippets or descriptions.
---

## INPUTS

- `Anti-Pattern Name` (string, required): The name of the anti-pattern to be documented.
- `Code Snippet or Description` (string, required): A code snippet or detailed description illustrating the anti-pattern.

## PRIMARY DIRECTIVE

Generate a comprehensive, actionable anti-pattern document that enables developers and automated code reviewers to reliably detect and avoid the specified anti-pattern. The document MUST follow the template structure defined in [antipattern.template.md](../skills/antipattern-templates/references/antipattern.template.md).

## WORKFLOW STEPS

### Step 1: Read the Template
Read the anti-pattern template at `../skills/antipattern-templates/references/antipattern.template.md` to understand all required sections and formatting conventions.

### Step 2: Clarify the Anti-Pattern
Before generating content, ensure you fully understand the anti-pattern:
- Review the provided `Anti-Pattern Name` and `Code Snippet or Description`
- Use web search to research the anti-pattern's common manifestations, root causes, and industry-standard terminology
- Ask the user clarifying questions if any of the following are unclear:
  - What programming language(s) does this apply to?
  - What are the specific symptoms or code characteristics?
  - Are there edge cases that look similar but are acceptable?
  - What is the measurable business/technical impact?

**Do NOT proceed to Step 3 until you have sufficient clarity.**

### Step 3: Generate Each Document Section
Generate content for each section defined in the template.

### Step 4: Self-Review Checklist
Before presenting to the user, verify:
- [ ] Detection instructions use EXACT syntax described in the template.
- [ ] Non-violation cases are listed BEFORE violation cases
- [ ] No vague language (e.g., "inappropriate", "bad", "should not") in detection rules
- [ ] Code examples are syntactically valid and realistic
- [ ] Scope uses valid glob/regex patterns

### Step 5: Present and Iterate
Present the complete document to the user. Ask:
> "Please review this anti-pattern document. Are the detection instructions specific enough to avoid false positives? Are there any edge cases I should add?"

Incorporate feedback and iterate until the user approves.

## CONSTRAINTS

- **DO NOT** use subjective language in Detection Instructions (e.g., "seems wrong", "looks suspicious")
- **DO NOT** skip the clarification step—incomplete understanding leads to vague detection rules
- **DO NOT** generate placeholder text—all sections must contain real, actionable content
- **DO NOT** combine violation and non-violation cases—maintain strict ordering

## OUTPUT

Save the final document as a Markdown file:
- **Location**: User-specified directory, or default to `docs/anti-patterns/`
- **Filename**: Kebab-case based on the anti-pattern name (e.g., `hardcoded-secrets.md`)
- **Format**: Structured exactly per the template with all sections completed