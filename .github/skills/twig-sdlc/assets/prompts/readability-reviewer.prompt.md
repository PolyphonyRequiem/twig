Review the plan at `{{ architect.output.plan_path }}` for readability.

## Expected Document Structure

The document should contain the following sections:
1. Executive Summary (one paragraph)
2. Background (current state, motivation, prior art, call-site audit if applicable)
3. Problem Statement (specific problems being solved)
4. Goals and Non-Goals
5. Requirements (functional and non-functional)
6. Proposed Design (architecture overview, key components, data flow, design decisions)
7. Alternatives Considered *(optional — include when non-obvious choices exist)*
8. Dependencies
9. Impact Analysis *(optional — include for multi-component or compatibility-sensitive changes)*
10. Security Considerations *(optional — include when security boundaries are affected)*
11. Risks and Mitigations *(optional — include for meaningful risks)*
12. Open Questions
13. Files Affected (new, modified, deleted)
14. ADO Work Item Structure (Issues, Tasks, acceptance criteria)
15. PR Groups (reviewable PR clusters with sizing and ordering)
16. References *(optional)*

## Evaluation Criteria

Focus exclusively on **structure and readability** — not technical accuracy:

| Criteria | Description |
|----------|-------------|
| **Document Structure** | Does it follow the expected sections? Is information logically organized? |
| **Clarity** | Is the writing clear, precise, and free of ambiguity? |
| **Audience Fit** | Is the detail level appropriate for engineers and AI agents? |
| **Decision Framing** | Are design decisions and open questions clearly framed with context? |
| **Cohesion** | Does the document read as a unified work with connected sections? |
| **Formatting** | Are tables, lists, and code references used effectively? |
| **Executive Summary** | Does it convey the problem, approach, and outcome in one paragraph? |
| **Plan Readability** | Are Issues, Tasks, and acceptance criteria clearly structured and easy to follow? |
| **Traceability** | Do Tasks trace back to requirements and design goals? |
| **Actionability** | After reading, would a developer or AI agent know exactly what to build? |

Do NOT evaluate technical correctness — a separate reviewer handles that.

## Scoring rubric (calibration)

- **95–100** — Document is clear and executable. `critical_issues = []`.
- **90–94** — Minor phrasing or formatting suggestions. `critical_issues = []`.
- **80–89** — Real structural problems (e.g., missing required section, confusing
  flow) that do not prevent execution. `critical_issues = []` unless an agent
  genuinely could not proceed.
- **<80** — Ambiguity severe enough to block execution. Populate `critical_issues`.

**The default expectation is 92–98.** A well-structured plan lands here even if
prose could be tightened. Do not dock for stylistic preferences.

## What counts as a CRITICAL issue

Populate `critical_issues` ONLY when:

- A required section is missing (sections 1–6, 8, 12–15 in the expected structure).
- Ambiguity makes it impossible for a developer or AI agent to proceed.
- Contradictory statements within the document.

Do NOT populate `critical_issues` for:

- Optional sections (7, 9, 10, 11, 16) being absent — they are explicitly optional.
- Suggested rewording, tone preferences, or formatting polish.
- "Could be clearer" or "would benefit from an example" — flag in `feedback`, not
  `critical_issues`.
- The absence of a section stating "No open questions" — if Open Questions section
  exists and lists none, that is sufficient; do not demand affirmative phrasing.

If no critical issues exist, set `critical_issues` to `[]`. That is the expected
outcome.
