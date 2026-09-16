---
command: proposal preview
group: plans
summary: Preview a proposal — journal import, pending snapshot, digest, and canApply gate.
stability: stable
mutates: local
---

# `twig proposal preview`

Reads a proposal v1 file, imports its journal row (if any), snapshots every
currently-staged pending change, and reports the canonical digest and the
`canApply` gate — the boolean that `proposal apply` will consult. Preview
never mutates ADO; the "local" mutation flag reflects that importing a
journal row is a write into the workspace's per-proposal store.

## Synopsis

```
twig proposal preview --file <path> [--full] [--interactive] [-o human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--file`|string|_none_|Path to the proposal v1 JSON file. Must resolve inside the current workspace root.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|
|`--full`|flag|`false`|Expand description bodies from the captured review. JSON is always exact.|
|`--interactive`|flag|`false`|Opt into a human-terminal Details/Back/Cancel review loop. Never authorizes or applies.|

## Behavior

Delegates to `IPlanLifecycleService.PreviewAsync`. Human output is a compact,
grouped review by default; every operation, precondition, effect, blocker and
authorization choice remains represented:

- **Canonical digest.** Recomputed exactly as validate reports it; this is
  the value the caller will pass to `proposal apply --confirm`.
- **Journal import.** The journal row keyed on the digest is loaded into the
  workspace store; a fresh proposal creates a row in its initial state, and
  a previously-imported proposal has its row refreshed.
- **Pending snapshot.** Every staged pending change is captured in exact
  staging order (`PlanPreviewResult.PendingChanges`). Any pending row makes
  `canApply` false — proposal v1 is declarative-only and will not
  auto-flush pending edits
  (`src/Twig.Domain/Services/Plan/PlanPreviewResult.cs:26-37`).
- **`canApply` gate.** True iff the proposal is valid, its workspace matches
  the active config, no pending row exists, and the journal was imported
  successfully. False otherwise, and the reason is on `Issues`.

### Review density and observations

- Scalar fields show **before → after** in aligned, shaded field/value blocks.
  Explicit clears, known absent values, empty strings and unknown baselines stay distinct.
- Description bodies are omitted in brief output, not their field effects. The
  `common-affix-replacement-v1` metric removes the identical prefix and suffix
  and counts the remaining **Unicode scalars** as removed/inserted spans. It is
  linear-time, constant-space, not minimal edit distance, and includes literal HTML.
  Unknown baselines have no numeric metric. The legend explains this once per review.
- Before observations come only from clean local cache data at the expected
  revision. Missing items/fields, revision mismatches and local edits are explicitly
  unknown; preview does not refresh ADO. Immediate cached parents are labeled
  context rather than counted as targets. Seeds retain staged identities and local
  display metadata, not fabricated published IDs or fingerprint attestations.
- `--full` exposes the captured exact source values, escaping terminal controls
  rather than executing them. `-o json` (also `json-full`/`json-compact`) retains
  the exact semantic `reviewModel` including strings and enrichment regardless of
  `--full`; no terminal markup or color instructions are added to canonical data.
- `--interactive` requires human output and TTY input **and** output. `Details`
  (`d`) expands, `Back` (`b`) returns to brief, and `Cancel` (`c`) or EOF closes
  review. Invalid input does nothing. All views reuse one captured observation;
  none refresh, authorize or apply. Without the flag, preview never prompts.
- Terminal output uses Twig type badges and configured icons. State text remains
  neutral on unknown terminal backgrounds; old/new colors have measured contrast
  against the owned field-block background. Pipes and capture writers stay plain.

## Examples

Review before authorization (commands only; the digest and effects depend on the file):

```bash
twig proposal preview --file .twig/proposals/close-1234.json
twig proposal preview --file .twig/proposals/close-1234.json --full
twig proposal preview --file .twig/proposals/close-1234.json --interactive
```

Machine preview for a host presenter:

```bash
twig proposal preview --file .twig/proposals/close-1234.json -o json
```

The envelope includes `digest`, `canApply`, `pendingChanges`, `operations`,
`issues`, and `reviewModel`. Hosts consume `reviewModel` rather than parsing
human layout. Its additive model-version-1 enrichment includes `contextItems`,
item URLs/parents/revisions, staged seed display metadata, and consequence
`fieldLabel`, `fieldType`, `before`, and `textChange` observations. See the
[shared review spec](../../specs/shared-proposal-review.md). Unknown model
versions must be refused rather than partially presented.

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Preview succeeded (even when `canApply=false` because of pending rows).|`0`|
|Proposal invalid — validation issues raised.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|
|`--interactive` with non-human output or redirected input/output.|`2` (before preview or journal import)|

## See also

- [`proposal validate`](proposal-validate.md) — cheaper check when you only need the digest.
- [`proposal apply`](proposal-apply.md) — consumes the digest reported here.
- [`pending`](pending.md) — same rows that block `canApply`.
- [`plan preview`](plan-preview.md) — deprecated alias.
