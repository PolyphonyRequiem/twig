# Review Guidelines

## Structure
- All design docs must include: Problem Statement, Proposed Solution,
  Alternatives Considered, Security Considerations, and Rollout Plan.
- Architecture Decision Records must follow the MADR template.
- Every document must have a clear introduction that states its purpose and audience.

## Terminology
- Use "service" not "microservice" unless specifically discussing the pattern.
- Define all acronyms on first use.
- Maintain a glossary section for documents with 5+ domain-specific terms.

## Clarity
- Target audience: senior engineers unfamiliar with this specific system.
- Avoid hedging language ("might", "could possibly", "it seems like").
- Each section should be self-contained enough to be read independently.
- Use active voice. Prefer "the system validates input" over "input is validated by the system".

## Completeness
- All claims must be supported with evidence, references, or reasoning.
- Edge cases and error scenarios must be explicitly addressed.
- Dependencies on external systems must be documented.

## Accuracy
- Version numbers, API names, and configuration values must be verifiable.
- Internal cross-references must resolve to actual sections or documents.
- Code examples must be syntactically valid and tested where possible.

## Code Examples
- All code blocks must specify a language.
- Examples must be syntactically valid.
- Include expected output or behavior for non-obvious examples.
- Use consistent formatting and style within each document.

## Formatting
- Use consistent heading levels (no skipping from ## to ####).
- Tables must have header rows and consistent column alignment.
- Lists should use consistent markers (all `-` or all `*`, not mixed).
- Links must include descriptive text (not "click here").
