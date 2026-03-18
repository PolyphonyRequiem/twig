---
name: conductor
description: Validate, run, and execute workflows. Use when orchestrating AI agents via YAML workflow files, executing an existing workflow, debugging execution, configuring routing between agents, setting up human-in-the-loop gates, or understanding workflow YAML schema. Only create new workflows when explicitly asked.
user-invokable: false
---

# Conductor

CLI tool for defining and running multi-agent workflows with the GitHub Copilot SDK or Anthropic Claude.

> **DO NOT create new workflow files unless the user explicitly asks you to create one.** Default to running, validating, or debugging existing workflows. If the user's request is ambiguous, assume they want to run or modify an existing workflow rather than create a new one.

Pre-built workflows are available in the `./workflows/` directory (relative to this skill). Look here when not provided a specific workflow file to run.

## Known Issues

**Windows: `No module named 'fcntl'`** — A regression in conductor's copilot provider (`_fix_pipe_blocking_mode()` in `src/conductor/providers/copilot.py`) unconditionally imports `fcntl`, a Unix-only module. The copilot provider itself supports Windows; only this specific method lacks a platform guard. To resolve:

1. **Update conductor** — run `conductor update` or `uv tool install --force git+https://github.com/microsoft/conductor.git` to get the latest version which may include a fix.
2. **If the error persists**, the fix has not yet been released upstream. Inform the user and suggest they file an issue at https://github.com/microsoft/conductor/issues referencing the `_fix_pipe_blocking_mode()` method.

## Error Handling

> **CRITICAL — do NOT improvise workarounds.** If `conductor` fails for any reason (installation failure, provider error, missing dependency, platform bug), you MUST:
>
> 1. **Report the exact error** to the user — include the full error message.
> 2. **Suggest the documented fix** from the Known Issues section above if it matches a known issue.
> 3. **Stop and wait for user direction.** Do NOT attempt to simulate, replicate, or approximate the multi-agent workflow yourself. The value of this skill is the structured multi-agent orchestration — a single-agent attempt is not an equivalent substitute.

## Setup

Conductor is installed automatically when needed. If a `conductor` command fails with "command not found", install it:

```bash
uv tool install git+https://github.com/microsoft/conductor.git
```

If `uv` is also missing, install it first: `curl -LsSf https://astral.sh/uv/install.sh | sh`

## Executing a Workflow

> **IMPORTANT:** Workflows are long-running (typically ~15 minutes). Launch the workflow in a **background terminal** (`isBackground=true`) and tell the user to watch the output there. **End the chat conversation immediately after starting the workflow.** Do NOT call `get_terminal_output` to check on it, do NOT read output files, do NOT narrate workflow progress. Trust that the command started and end your turn. See [references/execution.md](references/execution.md) for the full procedure.

## Quick Reference

```bash
conductor -V run workflow.yaml --input question="Hello"     # Execute (progress shown by default)
conductor -V run workflow.yaml --input question="Hello"  # Full verbose (untruncated prompts, tool args)
conductor run workflow.yaml --web --input q="Hello"      # Real-time web dashboard
conductor run workflow.yaml --web-bg --input q="Hello"   # Background mode (prints URL, exits)
conductor validate workflow.yaml                         # Validate only
conductor init my-workflow --template simple              # Create from template
conductor templates                                      # List templates
conductor stop                                           # Stop background workflow
conductor update                                         # Check for and install latest version
```

Progress output is shown by default. Use `-V` (verbose) for full prompts and detailed tool call info.

## When to Use Each Guide

**Creating or modifying workflows?** → See [references/authoring.md](references/authoring.md)
- Agent definitions, prompts, and output schemas
- Routing patterns (linear, conditional, loop-back)
- Parallel and for-each groups
- Human gates
- Context modes and MCP servers
- Cost tracking configuration

**Running or debugging workflows?** → See [references/execution.md](references/execution.md)
- CLI options and flags
- Debugging techniques
- Error troubleshooting
- Environment setup and providers

**Need complete YAML schema?** → See [references/yaml-schema.md](references/yaml-schema.md)
- All configuration fields with types and defaults
- Validation rules
- Type definitions

## Minimal Workflow Example

```yaml
workflow:
  name: my-workflow
  entry_point: answerer
  input:
    question: { type: string }

agents:
  - name: answerer
    prompt: "Answer: {{ workflow.input.question }}"
    output:
      answer: { type: string }
    routes:
      - to: $end

output:
  answer: "{{ answerer.output.answer }}"
```

For runtime config, context modes, limits, and cost tracking, see [references/authoring.md](references/authoring.md).

## Key Concepts

| Concept | Description |
|---------|-------------|
| `entry_point` | First agent/group to execute |
| `routes` | Where agent goes next (`$end` to finish, `self` to loop) |
| `parallel` | Static parallel groups (fixed agent list) |
| `for_each` | Dynamic parallel groups (runtime-determined array) |
| `human_gate` | Pauses for user decision with options |
| `context.mode` | How agents share data (accumulate, last_only, explicit) |
| `limits` | Safety bounds (max_iterations up to 500, timeout_seconds) |
| `cost` | Token usage and cost tracking configuration |
| `runtime` | Provider, model, temperature, max_tokens, MCP servers |
| `--web` | Real-time web dashboard with DAG graph, live streaming, in-browser human gates |

For pattern examples (linear, loop, conditional, parallel, for-each, human gate) and template syntax, see [references/authoring.md](references/authoring.md).

