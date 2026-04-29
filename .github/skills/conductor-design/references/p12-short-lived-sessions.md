# P12: Short-Lived Agent Sessions

Agent sessions should be designed short enough to **never trigger context compaction**.
Compaction is a design smell — it indicates the agent's scope is too broad or the
workflow is accumulating context that should be externalized. If an agent triggers
compaction, the workflow design must be revisited.

## Why This Matters

Context compaction degrades agent reasoning quality silently. When a model's context
window fills and earlier turns are summarized or dropped, the agent loses access to
precise details it may need. This is not a runtime problem to be tolerated — it is a
signal that the workflow architecture is wrong.

Short-lived sessions also improve:
- **Reproducibility** — smaller contexts are easier to test and debug
- **Cost efficiency** — fewer tokens per session means lower API costs
- **Reliability** — agents operating within context bounds produce more consistent output
- **Observability** — bounded sessions are easier to log and analyze

## Implications for Workflow Design

- Prefer recursive sub-workflows over single agents handling deep hierarchies
- Each agent should have a focused, bounded scope (one work item, one review, one decision)
- If a single work item requires extensive context (large codebase exploration), the
  agent should write findings to a file and pass the path — not accumulate in context
- Break "explore then act" patterns into two agents: one that explores and writes a
  summary file, another that reads the summary and acts
- Design workflows so no single agent session exceeds ~60% of available context

## Implications for Agent Prompts

- Prompts should instruct agents to externalize large outputs (write to file, not return inline)
- Agents performing iterative work should checkpoint progress to files between iterations
- System prompts should be concise — they consume context on every turn
- Avoid prompts that encourage open-ended exploration without bounds

## How to Detect Violations

1. **Runtime detection** — if an agent triggers compaction, it MUST log the event
2. **Design review** — workflows with agents that could plausibly exceed context should
   be flagged during review
3. **Empirical measurement** — track token usage per agent session over time; rising
   trends indicate scope creep

When compaction occurs:
1. It MUST be logged (tool, agent name, workflow, turn count, token usage)
2. The logged event should be queryable for future analysis
3. The workflow design should be revisited

## Logging Format Specification

Compaction events are written to `.conductor/logs/compaction-events.jsonl` (gitignored
but queryable for workflow design improvements).

Each entry is a single JSON line with the following fields:

```json
{
  "timestamp": "2025-01-15T14:32:01Z",
  "workflow": "sdlc-implement",
  "agent": "code-writer",
  "turnCount": 47,
  "approximateTokenUsage": 185000,
  "model": "claude-sonnet-4-20250514",
  "trigger": "context_compaction",
  "notes": "Optional human-added notes after investigation"
}
```

Required fields: `timestamp`, `workflow`, `agent`, `turnCount`, `approximateTokenUsage`,
`model`, `trigger`. Optional: `notes`.

## Examples

### Good Design

- A planning workflow breaks into: discover → write plan file → review plan (3 agents,
  each short-lived, passing state via files)
- A code implementation agent receives a focused task spec and writes code for one
  component — if it needs codebase context, a prior exploration agent wrote a summary
- An exploration agent writes findings to `.conductor/context/exploration-summary.md`
  and the downstream agent reads only that file

### Bad Design

- A single agent that explores a codebase, designs a solution, implements it, writes
  tests, and self-reviews — all in one session
- An agent that iterates on code review feedback for 20+ turns without checkpointing
- A planning agent that reads every file in a large directory to build context, then
  attempts to produce a plan in the same session
