# P10: Explicit Invariants

Each agent and script node must document its **invariants** — conditions that must
be true before, during, and after execution. Invariants are non-negotiable contracts
that the workflow enforces. Great effort should be taken to uphold them.

## Types of Invariants

- **Preconditions** — what must be true before the node executes (inputs valid,
  work item in expected state, branch exists, etc.)
- **Postconditions** — what must be true after the node completes (items created,
  tags written, state transitioned, etc.)
- **Loop invariants** — what remains true across iterations in retry/revision loops

## Implications

- System prompts and prompt templates should state invariants explicitly
- Downstream nodes may assert upstream postconditions as their preconditions
- Violations of invariants should surface as errors, not silent degradation
- Tests and verification steps should check invariants, not just outputs
