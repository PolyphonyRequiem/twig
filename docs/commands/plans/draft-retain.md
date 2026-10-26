# `twig draft retain`

Retain the exact staged intent of a recoverable native draft as a local native
proposal capture, without exporting, authorizing or applying it.

```bash
twig draft retain --stdin -o json < retain.json
twig draft retain --stdin -o minimal < retain.json
```

## Flags

| Flag | Contract |
|---|---|
| `--stdin` | Required. Read one request and wait for EOF. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

The request contains **only** `version: 1`, the unchanged `nativeContext`, and
decimal-string `generation`, `baselineRevision` and `observedRevision` from the
latest editing envelope. The 262144-byte UTF-8 and depth-32 limits apply;
duplicate or unknown members, rounded numbers and caller authorizer fields are
refused.

Native code rechecks the exact draft, baseline, origin, served evidence,
process gates and explicit Bench. Empty or fully restored-to-baseline intent is
refused as `draft-empty` without a journal row. On success RETAIN writes a
native capture and a `Planned` proposal journal row through the existing capture
store and journal transaction, or reuses an exact capture that is still
`Planned` with no authorization or confirmation evidence. This is a durable
local journal write (`mutates: local`), not a read.

The `retained` envelope carries `capture`: the editing context and decimal
tokens, native `digest`, `operationId`, `canonicalJson` **string** and ordered
`effects`. `authorized: false` and `canApply: false` describe this receipt; they
do not promise the journal stays `Planned`. RETAIN never exports a file,
flushes pending work, selects an active item, authorizes, applies or publishes.
Human and minimal output print `draft retained<TAB><digest><TAB><generation>`.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Retained (including unchanged exact reuse); `committed: true`. |
| `1` | Refused: `draft-empty`, `draft-invalid`, draft unavailable or CAS conflict, revision conflict, authority invalidated, non-editable field, cancellation, or `indeterminate` after a failed post-commit fence (`committed: true`, capture included when available; inspect rather than retry). |
| `2` | Usage: missing `--stdin`, invalid request shape, `invalid-json`, `input-too-large`, or an unsupported output format. |

## See also

- [`twig draft review`](draft-review.md), [`twig draft stage`](draft-stage.md),
  [`twig proposal apply`](proposal-apply.md)
