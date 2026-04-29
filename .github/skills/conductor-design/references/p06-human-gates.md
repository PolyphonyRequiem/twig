# P6: Human Gates for Genuine Decisions Only

Human gates are reserved for situations where:

1. **Agent confidence is low** — ambiguity cannot be resolved from code or work items
2. **Functional limitations** — user acceptance testing, manual validation required
3. **Decision points with multiple valid options** — e.g., architectural trade-offs
   during planning where 2-3 approaches are viable
4. **Irreversible actions** — destructive operations that warrant confirmation

Human gates must **not** be used for:
- Routine progress checkpoints
- Confirmation of work the agent is confident about
- Compensating for bugs or missing automation

When a gate is presented, it should include enough context for the human to decide
without investigating independently.

## Confidence Thresholds by Phase

The bar for triggering a human gate varies by lifecycle phase. Planning decisions
are cheaper to revisit; implementation errors are expensive to undo.

- **Planning phase** — trigger gates at **≤85% confidence**. Design decisions
  benefit from human steering. Open questions, architectural trade-offs, and
  scope ambiguity should surface early.
- **Implementation and beyond** — trigger gates at **≥95% confidence only**.
  By this phase, the plan is approved and the work is mechanical. Gates should
  be reserved for genuine blockers: user acceptance testing, irreversible
  destructive actions, or situations where the agent truly cannot proceed.
