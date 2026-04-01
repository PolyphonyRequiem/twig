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
Score the plan 0-100. Be specific about issues.
