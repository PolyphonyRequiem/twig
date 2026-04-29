# P8: Prefer Scripts Over Agents for Deterministic Logic

When a decision is deterministic and the implementation is straightforward, use a
script (PowerShell, Python, etc.) rather than an LLM agent. Agents are for judgment,
ambiguity resolution, and creative work — not for `if/else` routing or data
transformation that can be expressed as code.

## Implications

- State detection, phase routing, and input validation are scripts
- PG grouping by tag, work tree loading, and plan file parsing are scripts
- Architectural decisions, code review, and user-facing content are agents
- If you find yourself writing a prompt that says "output X if condition Y" with
  no ambiguity, it should be a script
