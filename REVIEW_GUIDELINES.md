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

## Telemetry & Data Privacy

Twig supports optional anonymous telemetry via environment variable opt-in. The following
rules are **non-negotiable** and apply to all code that emits, collects, or transmits
telemetry data:

### NEVER send (even hashed)
- Organization names, project names, or team names
- User names, display names, or email addresses
- Process template names (e.g., "Agile", "Scrum", "CMMI")
- Work item type names (e.g., "User Story", "Bug", "Task")
- Field names or reference names (e.g., "Microsoft.VSTS.Scheduling.StoryPoints")
- Area paths or iteration paths
- Work item IDs, titles, descriptions, or any content
- Repository names, branch names, or commit hashes
- Any ADO-specific identifiers or process-specific information

### Safe to send
- Twig command name (e.g., "status", "tree", "refresh")
- Command duration in milliseconds
- Exit code (0/1)
- Output format (human/json/minimal)
- Twig version string
- OS platform (win/linux/osx)
- Generic boolean flags (e.g., `had_profile`, `merge_needed`, `hash_changed`)
- Generic numeric counts (e.g., `field_count: 47`, `item_count: 12` — numbers only, no identifiers)

### Enforcement
- All telemetry property keys must pass an allowlist check in tests
- Any key containing "org", "project", "user", "type", "name", "path", "template",
  "field", "title", "area", "iteration", or "repo" must be rejected
- Telemetry must be completely disabled (zero network calls) when the
  `TWIG_TELEMETRY_ENDPOINT` environment variable is unset
- Telemetry failures must never affect command execution or return codes
