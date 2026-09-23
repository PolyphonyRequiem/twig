---
command: proposal preview
group: plans
summary: Preview a proposal — digest, canApply gate, and optional native presentation.
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

```sh
twig proposal preview --file <path> [--full] [--expect-digest <digest>] [--interactive] [--include-rendering --width <columns> --color <always|never>] [-o human|json|minimal]
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
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`. The rendering opt-in requires `json`.|
|`--full`|flag|`false`|Expand description bodies from the captured review. JSON is always exact.|
|`--interactive`|flag|`false`|Opt into a human-terminal Details/Back/Cancel review loop. Never authorizes or applies.|
|`--include-rendering`|flag|`false`|With JSON, include presentation version 1 (`brief` and `full`) rendered by native Twig from the same observation.|
|`--width`|integer|`unbounded / 120`|Presentation width; omitted human output is unbounded, while an included JSON frame defaults to 120. Explicit values are inclusive 20..400.|
|`--color`|string|`never`|Presentation color mode: `always` or `never`; OMP requests `always` unless `NO_COLOR` requires `never`.|
|`--expect-digest`|string|_none_|Require an exact canonical SHA-256 digest match before import or review rendering.|

## Behavior

Delegates to `IPlanLifecycleService.PreviewAsync`. Human output is a compact, grouped review
of material effects, warnings and blockers, without approval choices or sign-off instructions.
The human projection omits the generic title, digest, workspace, operation identities,
preconditions and provenance; structured `json`/`minimal` output retains those exact fields,
including authorization choices, for hosts and separate apply authorization.

- **Canonical digest.** Recomputed exactly as validate reports it; this is
  the value the caller will pass to `proposal apply --confirm`.
- **Journal import.** The journal row keyed on the digest is loaded into the
  workspace store; a fresh proposal creates a row in its initial state, and a re-preview
  updates only the separate last-preview ordering timestamp. Original `previewed_at` and
  lifecycle state remain unchanged.
- **Digest guard.** When `--expect-digest` is supplied, the current canonical digest must
  match exactly. A mismatch returns a clear issue before journal import or review-model
  construction, so neither an invalid proposal nor stale bytes can move latest selection.
- **Pending snapshot.** Every staged pending change is captured in exact
  staging order (`PlanPreviewResult.PendingChanges`). Any pending row makes
  `canApply` false — proposal v1 is declarative-only and will not
  auto-flush pending edits
  (`src/Twig.Domain/Services/Plan/PlanPreviewResult.cs:26-37`).
- **`canApply` gate.** True iff the proposal is valid, its workspace matches
  the active config, no pending row exists, and the journal was imported
  successfully. False otherwise, and the reason is on `Issues`.

### Native presentation for OMP

The OMP presenter's single `twig_proposal_render` call invokes the native presentation envelope with this exact child-process request; do not run the CLI command separately before or after the tool:

```sh
twig proposal preview --file <absolute-file> -o json --include-rendering --width 100 --color always
```

The envelope keeps the existing digest, `canApply`, issues, operations, pending changes and `reviewModel`; the additive presentation is version 1 and contains brief/full frames from the same single observation. Hosts must retain the structured result and exact workspace/digest, not parse ANSI. The interactive `twig_proposal_render({file,workspace?,full?})` tool switches retained frames without another preview. Closing or viewing details is never approval or apply. Feature support is proven by response validation; no minimum native version is pinned here.

OMP shows the retained native frames inside a rounded, scrollable, theme-colored **read-only** window. Its header and controls stay visible in narrow terminals; the native preview is sized for the window's inner width instead of wrapping a second time. The window adds no new approval action or rendering authority.

If the OMP companion or tool is unavailable before a call, use the ordinary complete structured/plain review and disclose the fallback; a direct CLI preview is allowed only for that pre-call fallback. If a call begins but its response is incompatible, failed or truncated, stop the affected authorization and report the failure; do not recover by ANSI parsing, another preview, tree-set, sync or apply.

### Review density and observations

- Scalar fields show **before → after** in aligned, shaded field/value blocks.
  Explicit clears, known absent values, empty strings and unknown baselines stay distinct.
- Description bodies are omitted in brief output, not their field effects. The
  `common-affix-replacement-v1` metric removes the identical prefix and suffix
  and counts the remaining **Unicode scalars** as removed/inserted spans. It is
  linear-time, constant-space, not minimal edit distance, and includes literal HTML.
  Unknown baselines have no numeric metric. Human output explains the units once per relevant review;
  JSON retains the exact metric identifier.
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
  against the owned field-block background. With the default `--color never`,
  pipes and capture writers stay plain; `--color always` keeps ANSI inside JSON
  presentation frame strings only.

## Examples

Review before authorization (commands only; the digest and effects depend on the file):

```bash
twig proposal preview --file .twig/proposals/close-1234.json
twig proposal preview --file .twig/proposals/close-1234.json --full
twig proposal preview --file .twig/proposals/close-1234.json --interactive
twig proposal preview --file .twig/proposals/close-1234.json --expect-digest 3f9c...a1b7 --interactive
```

Machine preview for a host presenter:

```bash
twig proposal preview --file .twig/proposals/close-1234.json -o json
```

For the OMP rendered route, the tool's one native child-process request is shown as an implementation reference (call `twig_proposal_render` once, not this command separately):

```bash
twig proposal preview --file /absolute/workspace/.twig/proposals/close-1234.json -o json --include-rendering --width 100 --color always
```

The envelope includes `digest`, `canApply`, `pendingChanges`, `operations`, `issues`, and `reviewModel`. With `--include-rendering`, it adds presentation version 1 (`brief` and `full`) from the same observation. Hosts consume the structured model and retained frames rather than parsing human layout or ANSI. See the [presentation reference](../../../.github/skills/twig/references/presentation.md) for the OMP handoff and fallback rules. Unknown model or presentation versions must be refused rather than partially presented.

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Preview succeeded (even when `canApply=false` because of pending rows).|`0`|
|Proposal invalid — validation issues raised.|`1`|
|`--file` omitted, or file path could not be resolved.|`2`|
|`--interactive` with non-human output or redirected input/output.|`2` (before preview or journal import)|
|Invalid rendering options — non-JSON output with `--include-rendering`, width outside 20..400, or unsupported color value.|`2`|

## See also

- [`proposal validate`](proposal-validate.md) — cheaper check when you only need the digest.
- [`proposal apply`](proposal-apply.md) — consumes the digest reported here.
- [`pending`](pending.md) — same rows that block `canApply`.
- [`plan preview`](plan-preview.md) — deprecated alias.
