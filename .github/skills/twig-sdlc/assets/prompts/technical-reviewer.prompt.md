Review the implementation plan at `{{ architect.output.plan_path }}`.
**Context:** Implementing #{{ intake.output.epic_id }} — {{ intake.output.epic_title }}

## Fact-Checking

To verify the document's claims, independently check:
- Technical claims by reading referenced source files and project configuration
- File paths, API names, and type references exist in the actual codebase
- Whether the described current state actually matches the codebase
- Whether the proposed design is feasible given AOT/trim constraints
- Whether dependencies and sequencing are realistic

## Evaluation Criteria

Focus exclusively on **technical content** — accuracy, correctness, and completeness:

| Criteria | Description |
|----------|-------------|
| **Technical Accuracy** | Are file paths, API references, and patterns correct? |
| **Codebase Grounding** | Is the design grounded in the actual codebase, not aspirational? |
| **Completeness** | Are all aspects of the requirement addressed? Tests included? |
| **Design Soundness** | Are architectural decisions well-reasoned and defensible? |
| **Alternatives Analysis** | Were alternatives fairly evaluated with honest trade-offs? |
| **Impact Analysis** | Are all affected components identified? Side effects considered? |
| **Risk Assessment** | Are risks realistic? Mitigations concrete and actionable? |
| **Feasibility** | Can this design be implemented given AOT, trim, and project constraints? |
| **Plan Actionability** | Are Issues/Tasks properly scoped, sequenced, and actionable? |
| **Dependency Management** | Are prerequisites clearly identified between Issues/PR groups? |

Do NOT evaluate structure, readability, or formatting — a separate reviewer handles that.

Research the actual codebase to verify claims in the plan.

## Scoring rubric (calibration)

- **95–100** — No issues found. Plan is executable as-is.
- **90–94** — Minor non-blocking suggestions (better phrasing, optional alternatives).
  DO NOT populate `critical_issues`.
- **80–89** — Real problems that should be addressed but do not block execution
  (e.g., missing non-critical section, underspecified risk). DO NOT populate
  `critical_issues` unless a reasonable engineer could not proceed.
- **<80** — Blocking problems. Populate `critical_issues` with each one.

The **default expectation is 92–98**. A plan that is technically sound and actionable
should land here even if you personally would have written it differently. Do not
dock for taste, alternative approaches, or polish — those belong in `feedback`, not
in `critical_issues` and not in the score.

## What counts as a CRITICAL issue (for the `critical_issues` array)

Populate `critical_issues` ONLY when one or more of the following is true:

- A factual error that would cause implementation to fail (wrong file path,
  non-existent API, incompatible constraint).
- A missing required section that a downstream agent needs (e.g., ADO Work Item
  Structure, Files Affected) — not an optional section.
- An infeasibility given AOT/trim constraints.
- Ambiguity so severe that a reasonable implementer could not proceed.

Do NOT populate `critical_issues` for:

- Style, phrasing, or document polish.
- Optional sections that are absent (Alternatives, Security, Risks — these are
  explicitly optional per the architect prompt).
- Additional analysis you would have liked to see but is not required.
- Anything you would describe as "consider" or "could be enhanced".

If no critical issues exist, set `critical_issues` to `[]`. This is the expected
outcome for a well-formed plan. Finding nothing to flag is a valid result.
