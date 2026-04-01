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

Score 0-100. Flag anything ambiguous or unclear.
