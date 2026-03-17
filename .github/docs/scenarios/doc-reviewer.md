# octane-doc-review — Multi-Agent Document Review

A multi-agent deliberation system for reviewing technical documentation. Multiple LLM reviewers analyze your document from different perspectives, debate their findings through structured rounds, and produce a revised document alongside an auditable review report.

> **Name mapping**
> - Skill name: `octane-doc-review`
> - Slash command: `/octane-doc-review`
> - Workflow file: `assets/doc-review.yaml`
> - Scenario folder: `artifacts/scenarios/doc-reviewer/`

## When to Use

- Review a technical design doc before sharing with stakeholders
- Get multi-perspective feedback on API documentation or specifications
- Audit onboarding guides, runbooks, or architecture docs for gaps
- Surface clarity and completeness issues that a single reviewer would miss
- Produce an auditable review report for compliance or team retrospectives

## Prerequisites

- **Conductor skill** — the `conductor` shared skill must be installed. Conductor is the multi-agent orchestration engine that coordinates the reviewer agents. Verify it is available with `conductor --version`. See `~/.copilot/skills/conductor/SKILL.md` for setup.
- **MCP filesystem server** — `@modelcontextprotocol/server-filesystem` (provides `read_file`, `write_file`, `create_directory`). Included in the Conductor workflow configuration.
- **Node.js 18+** — required for `npx` to launch the MCP server.

## Security & Privacy

- The full document content (and guidelines, if provided) is sent to the configured model providers for review.
- Output artifacts (revised document and review report) are written to disk and may contain excerpts and derived content from the original document.
- Do not use on documents containing credentials, secrets, or regulated data without verifying your organization's AI usage policies.

## Overview

Unlike single-pass LLM review, `octane-doc-review` uses structured multi-perspective deliberation. Different models bring different strengths and biases — the debate process surfaces issues that any single model would miss.

### Architecture

```
dispatcher ──→ initial_review (parallel) ──→ deliberation (sequential loop)
                  │                              │
                  ├── reviewer_1 (Opus)          ├── deliberation_reviewer_1 ──→ deliberation_reviewer_2 ──→ arbitrator
                  └── reviewer_2 (GPT-5.2)      │                                                             │
                                                 │    consensus? ──→ proofreader ──→ output_writer ──→ $end
                                                 │    max rounds? ──→ proofreader ──→ output_writer ──→ $end
                                                 └──  otherwise   ──→ loop back to deliberation
```

(`$end` denotes workflow completion in Conductor.)

> **Note:** `reviewer_1`/`reviewer_2` in the initial review phase are distinct agents from `deliberation_reviewer_1`/`deliberation_reviewer_2` in the deliberation loop, though they use the same underlying models.

| Phase | Agent(s) | Model | Purpose |
|-------|----------|-------|---------|
| Dispatch | `dispatcher` | Claude Sonnet 4.6 | Reads document and guidelines via MCP filesystem |
| Initial Review | `reviewer_1`, `reviewer_2` | Opus 4.6, GPT-5.2 | Parallel independent analysis |
| Deliberation | `deliberation_reviewer_1`, `deliberation_reviewer_2` | Opus 4.6, GPT-5.2 | Sequential debate responding to each other's feedback |
| Arbitration | `arbitrator` | Opus 4.6 | Evaluates consensus, incorporates agreed changes, decides whether to loop |
| Proofreading | `proofreader` | Opus 4.6 | Final quality pass comparing revised doc against original |
| Output | `output_writer` | Sonnet 4.6 | Writes revised document and review report to disk |

## Workflow

1. **Prepare your document** — have a Markdown file (`.md`) ready for review. A default `REVIEW_GUIDELINES.md` is bundled with the skill; optionally override it with your own at the repo root.
2. **Run the review** — invoke via slash command or Conductor CLI (see below). Optionally specify focus areas, exclusions, or max deliberation rounds.
3. **Monitor progress** — the `--web-bg` flag opens a real-time dashboard showing agent activity. Runtime depends on document length and deliberation rounds; a short document (~1,000 words) with `max_rounds=2` typically completes in under 5 minutes, while longer documents or more rounds may take up to 10 minutes. Each deliberation round involves 3 agent calls (2 deliberation reviewers + arbitrator), plus fixed overhead for the dispatcher, proofreader, and output writer.
4. **Review output** — find the revised document (`<name>.revised.md`) and review report (`<name>.review-report.md`) next to the input document (or in `output_dir` if specified).

## Example Prompts

### Via Slash Command

The `/octane-doc-review` slash command is available when the skill is installed via Octane (`octane install doc-reviewer`). Trailing text after the document path is mapped to the `focus` input. For advanced parameters (`max_rounds`, `exclude`), use the Conductor CLI.

```
/octane-doc-review review docs/auth-redesign.md focusing on security and clarity
```

### Via Conductor CLI

```bash
# Set the workflow path for convenience
WORKFLOW="artifacts/scenarios/doc-reviewer/skills/octane-doc-review/assets/doc-review.yaml"

# Basic review
conductor --silent run "$WORKFLOW" \
  --input document_path="docs/auth-redesign.md" --web-bg

# With focus areas
conductor --silent run "$WORKFLOW" \
  --input document_path="docs/api-spec.md" \
  --input focus="Focus on security and completeness" --web-bg

# Limited rounds
conductor --silent run "$WORKFLOW" \
  --input document_path="docs/design.md" \
  --input max_rounds=2 --web-bg

# Excluding specific review aspects
conductor --silent run "$WORKFLOW" \
  --input document_path="docs/design.md" \
  --input exclude='["formatting","actionability"]' --web-bg
```

## Configuration Reference

### Workflow Inputs

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `document_path` | string | Yes | — | Path to the Markdown document to review |
| `focus` | string | No | `""` | Natural language review focus (e.g., `"Focus on security and clarity"`). Empty means general review across all aspects. |
| `exclude` | array | No | `[]` | JSON array of review aspects to de-prioritize (e.g., `'["formatting"]'`). Values must match aspect keys: `structure`, `clarity`, `completeness`, `accuracy`, `actionability`, `formatting`. |
| `guidelines_path` | string | No | `REVIEW_GUIDELINES.md` | Path to review guidelines file |
| `max_rounds` | number | No | `2` | Maximum deliberation rounds. Must be an integer in 1–20; invalid values cause a validation error. |
| `output_dir` | string | No | `""` (same dir as input) | Output directory for artifacts. If empty, writes next to the input document. |

### Path Resolution

- All relative paths (`document_path`, `guidelines_path`, `output_dir`) are resolved from the directory where `conductor run` is invoked.
- Absolute paths are supported.
- The `output_dir` is created automatically if it does not exist.

### Review Aspects

| Aspect | What it covers |
|--------|---------------|
| `structure` | Missing sections, poor organization, unclear audience |
| `clarity` | Ambiguous language, undefined jargon, unstated assumptions |
| `completeness` | Gaps in reasoning, missing edge cases, unanswered questions |
| `accuracy` | Internal contradictions, unsupported claims |
| `actionability` | Vague next steps, unclear ownership, missing success criteria |
| `formatting` | Broken links, inconsistent heading levels, markdown issues |

### Project Configuration (`.doc-review.yaml`)

Place a `.doc-review.yaml` file in your repo root or project subdirectory to set persistent defaults:

```yaml
# .doc-review.yaml
focus: "Focus on security and completeness"
max_rounds: 2
output_dir: "reviews/"  # override: write to a dedicated directory instead of next to input
guidelines_path: "REVIEW_GUIDELINES.md"
exclude:
  - formatting
```

See the template file at the repo root for the full schema.

Configuration hierarchy (highest priority wins):
1. Workflow inputs (`--input` or natural language)
2. Project-level config (`.doc-review.yaml` in project subfolder)
3. Repo-level config (`.doc-review.yaml` in repo root)
4. Workflow defaults

### Review Guidelines (`REVIEW_GUIDELINES.md`)

A default `REVIEW_GUIDELINES.md` is bundled in the skill's [`assets/`](skills/octane-doc-review/assets/) directory and is used automatically for workspace installs (`.github/skills/octane-doc-review/`). To customize, either:
- **Override**: Place your own `REVIEW_GUIDELINES.md` at your repo root (takes precedence over the bundled default)
- **Pass explicitly**: Use `guidelines_path` to point to any guidelines file

> **Note:** Bundled defaults only apply to workspace installs. For user-level installs (`~/.copilot/skills/`), the MCP filesystem server cannot access the skill directory — copy `REVIEW_GUIDELINES.md` to your repo root or use `guidelines_path`.

Guidelines support additive merge — subdirectory guidelines add to root guidelines but cannot remove root-level rules.

## Expected Output

Two artifacts are produced next to the input document by default (or in `output_dir` if specified):

### `<name>.revised.md`

The final proofread document with all agreed changes incorporated.

### `<name>.review-report.md`

Structured review report containing:

- **Summary** — High-level review outcome
- **Review Configuration** — Focus areas, rounds, consensus status
- **Initial Reviews** — Each reviewer's feedback with quality scores (1–10 scale across review aspects)
- **Deliberation Summary** — Round-by-round outcomes
- **Final Disposition** — Agreed changes and unresolved disagreements with positions
- **Proofreader Notes** — Final observations from the proofreading pass

Findings are labeled with stable IDs within a single run and referenced consistently across deliberation and disposition sections. The report includes an explicit consensus status (reached/not reached) with a brief reason when consensus was not reached.

The workflow's JSON result includes `revised_document_path` and `review_report_path` for programmatic consumers. When run via an agent (e.g., Copilot), the agent should display these paths and open the files in the editor if available.

## Failure Handling

| Scenario | Behavior | Artifacts Written |
|----------|----------|-------------------|
| Single reviewer fails | Continues with remaining reviewer(s) | Yes — revised doc and report reflect available reviews |
| All reviewers fail | Workflow aborts (no partial output) | No |
| Arbitrator fails | Workflow aborts; initial reviews are preserved in logs but no revised document or report is produced | No |
| Proofreader fails | Outputs arbitrator's revised document as-is, flagged as unproofread | Yes — revised doc (unproofread) and report |
| Max rounds reached | Arbitrator produces "agree to disagree" summary; only agreed changes applied | Yes — revised doc and report |
| Guidelines file not found | Continues without guidelines (non-blocking) | Yes |
| Document not found | Workflow aborts with clear error | No |

On failure, Conductor returns a non-zero exit code and logs identify the failing agent by name.

## Troubleshooting

**Workflow runs too long**: Reduce `max_rounds` to 2–3. Each deliberation round involves 3 agent calls (2 deliberation reviewers + arbitrator), plus fixed overhead for the dispatcher, proofreader, and output writer. Runtime depends on document size, model latency, and `max_rounds`.

**Reviewers agree on everything in round 1**: This is normal for well-written documents. The workflow completes quickly.

**Output directory not created**: Ensure the MCP filesystem server has write access to the target path.

**Guidelines not loading**: Check the file path. Default is `REVIEW_GUIDELINES.md` in the repo root. Use `guidelines_path` input for custom locations. Relative paths resolve from where `conductor run` is invoked.

**Validation errors**: Run `conductor validate "$WORKFLOW"` to check YAML syntax against the Conductor schema.

