# `twig draft capture`

Legacy explicit capture: retain previously inspected native draft intent as a
local proposal journal and export its canonical proposal bytes to a file. It
never authorizes or applies.

```text
twig draft capture 42 --bench Sprint --checkout-root <root> --connection-id <connection> --binding-id <binding> --identity-id <identity> --generation 1 --revision 3 -o json
twig draft capture 42 --bench Sprint --checkout-root <root> --connection-id <connection> --binding-id <binding> --identity-id <identity> --generation 1 --revision 3 --recover <digest> -o json
```

## Flags

| Flag | Contract |
|---|---|
| `<id>` | Required positive published native ADO work-item ID. |
| `--bench` | Required explicit saved Bench. |
| `--checkout-root`, `--connection-id`, `--binding-id`, `--identity-id` | Required qualified native identity; a changed origin, binding or identity refuses. |
| `--generation`, `--revision` | Required exact draft generation (≥ 0) and revision (> 0); the draft's baseline and observed revisions must both equal `--revision`. |
| `--recover` | Optional digest of a retained capture whose export did not complete. |
| `-o` | `human` (default), `minimal` or `json`. |

## Behavior

CAPTURE admits the explicit Bench with the expected binding and identity,
requires the exact draft generation and revision, and checks the served
evidence Bench. It reads the cached item without calling the editor, a work
metadata provider or proposal preview. It then writes a `Planned` proposal
journal row and native capture, marks export incomplete, writes the canonical
proposal to `<checkout-root>/.twig/proposals/`, and marks export complete.

With `--recover`, no new capture is created: the retained capture must match
the exact draft, origin, generation and revision and still be inspection-only
current, and only its export is redone.

Local effects: journal row, export-state field and the exported proposal file
(`mutates: local`). It never writes pending changes, authorizes, applies or
publishes. Output reports `status: exported-local-intent`, the digest and file,
`authorized: false` and `canApply: false`; minimal prints
`<digest><TAB><file>`.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Captured (or recovered) and exported. |
| `1` | Refused before commit (stale draft or Bench, origin changed, missing draft or cached item, recovery mismatch or stale), or `export-incomplete` after commit: the capture is retained and the message names `--recover <digest>`. |
| `2` | Usage: invalid output format or missing/invalid identity, generation or revision. |

Cancellation before the capture commits propagates to the CLI cancellation
handler; after commit it is reported as `export-incomplete` with exit 1.

## See also

- [`twig draft retain`](draft-retain.md) (the native editing successor),
  [`twig proposal preview`](proposal-preview.md)
