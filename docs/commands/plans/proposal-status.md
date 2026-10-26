---
command: proposal status
group: plans
summary: Show journal state for a proposal file, keyed on its digest.
stability: stable
mutates: none
---

# `twig proposal status`

Reads the workspace's per-proposal journal and reports the current state
for the file's digest — the top-level plan state, per-operation states in
declaration order, any terminal error captured on apply. Cache-only; makes
no provider calls. A valid v2 file has no journal row because v2 preview does
not import one.

## Synopsis

```
twig proposal status --file <path> [-o human|json|minimal]
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
|`--file`|string|_none_|Path to the proposal JSON file. Must resolve inside the current workspace root.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPlanLifecycleService.StatusAsync`
(`src/Twig/Commands/PlanCommand.cs:145-166`). The result carries three
distinct shapes and the exit code discriminates between them
(`src/Twig.Domain/Services/Plan/PlanStatusResult.cs:6-22`):

1. **Journal loaded.** `Found=true` with `Digest`, `State`, `Operations`,
   and (on prior apply failure) `Error` populated. Exit 0.
2. **Valid digest, no journal.** The lifecycle service returns `null` and
   the command emits a `proposalStatusNotFound` document
   (`src/Twig/Commands/PlanCommand.cs:154-158`). Exit 1 — the file parsed
   cleanly but has no imported journal row. Version 2 previews intentionally
   do not import one.
3. **Input error.** The lifecycle service returns a non-null result with
   `Found=false` and `Issues` populated (path outside workspace, unreadable
   file, invalid JSON, workspace mismatch). Exit 2.

Status never mutates the journal. If the digest has moved because the file
was re-edited, re-run `proposal preview` to import the current row before
consulting status.

### Retained capture paths and directory aliases

A native draft capture retains the file's canonical physical path: every
symlink or junction component is resolved before the row is written, so a
capture made through an aliased checkout or proposals directory records the
file it actually wrote.

Rows retained by earlier builds may hold an alias spelling instead. Status
still reads them: the retained path is resolved and contained inside the
workspace, then compared with the file's canonical path. A file overwritten
under an aliased row is reported as a replacement, whichever spelling you pass.
These rows are read in place — status never rewrites a retained path or
changes a digest. Matching paths do not bypass the origin, revision, or
fingerprint checks that follow.

A retained or requested path is refused, with a named reason, when it resolves
outside the workspace, when a component cannot be inspected, or when link
resolution exceeds the depth limit (a link loop). Only genuinely missing
trailing segments are tolerated. A legacy row cannot prove where its file
physically was when captured, so re-capturing a draft whose row retains an old
alias spelling is refused rather than silently rebinding that history.

## Examples

Human status for a previewed but not-yet-applied proposal:

```console
$ twig proposal status --file .twig/proposals/close-1234.json
digest: 3f9c…a1b7
state:  Planned
  [0] close Batch → Planned  (not-started)
```

Machine snapshot for an agent:

```console
$ twig proposal status --file .twig/proposals/close-1234.json -o json
{
  "digest": "3f9c…a1b7",
  "found": true,
  "state": "Planned",
  "operations": [ /* ordinal + state + diagnostics + full evidence */ ],
  "error": null
}
```

### Per-operation `diagnostics`

Every row in the JSON payload carries value-free `diagnostics` beside the
existing `resultJson`, `warning`, and `error` keys (`result` in MCP):

```json
"diagnostics": {
  "disposition": "verified",
  "code": "verified",
  "expectedRevision": 2,
  "observedRevision": 3,
  "fields": [{ "field": "System.Title", "classification": "exact" }],
  "missingFields": [],
  "summary": "verified: 1 checked field(s): System.Title (exact)"
}
```

The projection is derived by
`Twig.Domain.Services.Plan.PlanJournalOperation.Diagnostics` from journal `State`
plus a defensive parse of `ResultJson` and (as a fallback for `expectedRevision`)
`RequestJson` — a row that predates the additive payload keeps its known
`disposition` and leaves `code` null rather than fabricating detail. Vocabularies:

| Key | Values |
|---|---|
| `disposition` | `verified`, `failed`, `outcome-unknown`, `in-flight`, `awaiting-verification`, `not-started` |
| `code` | `verified`, `field-mismatch`, `missing-required-fields`, `revision-not-advanced`, `revision-conflict`, `readback-unavailable`, or `null` |
| `fields[].classification` | `exact`, `cleared`, `canonicalized-html`, `canonicalized-identity`, `server-generated`, `mismatch`, `clear-failed` |

For HTML fields, `canonicalized-html` can explain server-added trailing ASCII
whitespace at ordinary block edges. Styling or locally rendered void elements
keep their entire top-level subtree strict, without disabling normalization of
unrelated ordinary sibling blocks. Inline word boundaries, preformatted text,
links, and field clears remain significant; unknown or ambiguous markup and
stylesheet elements retain conservative comparison. An existing indeterminate
operation still requires native readback reconciliation, not replay of its write.

When ADO refuses a stale revision without disclosing its current revision,
`observedRevision` is `null` and the summary labels it unknown. The adapter's
zero sentinel is not a server revision. The operation remains `failed`; this
does not trigger another read or retry the refused write.

Human and minimal output render disposition, revisions, and a bounded summary
of at most eight field entries; JSON retains the complete field lists.
The `full evidence: rerun with '-o json' …` hint points to the original
per-operation `warning` and `error` bodies, which can carry ADO response fragments with
user-authored values. The JSON `resultJson`/`warning`/`error` keys remain the
full-evidence route (AB#881).

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Journal row loaded for the file's digest.|`0`|
|File parsed and yielded a digest, but no journal has ever been imported for it.|`1`|
|Input error — path outside workspace, unreadable file, invalid JSON, workspace mismatch.|`2`|

## See also

- [`proposal preview`](proposal-preview.md) — imports the journal row this command reads.
- [`proposal apply`](proposal-apply.md) — writes the row this command reports.
- [`plan status`](plan-status.md) — deprecated alias.
