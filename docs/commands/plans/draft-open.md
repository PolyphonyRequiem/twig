# `twig draft open`

Open the shared recoverable native draft for one published ADO item, using an
explicitly named saved Bench and the native served ADO form. Emits editing
envelope **version 1**, which is separate from the semantic detail and
`draft show` inspection documents.

```text
twig draft open 42 --bench Sprint
twig draft open 42 --bench 7 --expect-binding <binding> --expect-identity <identity> -o json
```

## Flags

| Flag | Contract |
|---|---|
| `<id>` | Required positive native ADO work-item ID. Seeds and provider-neutral IDs are refused. |
| `--bench` | Required explicit saved Bench (name or ID). The current Bench is never switched. |
| `--expect-binding`, `--expect-identity` | Optional exact native binding and identity tokens; a mismatch refuses instead of rebinding. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

OPEN admits the explicit Bench through native operation admission, inspects the
native served form layout and process rules, and checks that the cached item has
no local pending work. Within one local SQLite transaction it records the served
evidence (an `INSERT OR REPLACE` of local metadata) and creates the draft, or
observes an existing draft by compare-and-swap. It never refreshes the item,
switches the current Bench, writes pending changes, creates a proposal journal,
grants Save/Apply authority, or publishes to ADO.

The envelope carries the unchanged `nativeContext` and decimal-string
`generation`, `baselineRevision` and `observedRevision` tokens that STAGE and
RETAIN require. The ordered form and unavailable/read-only reasons remain
visible. Unavailable served metadata is reported as a read-only observation,
never as positive eligibility. Unsupported providers and fields are refused
explicitly.

Local effects: served-evidence metadata and the recoverable draft row
(`mutates: local`). Remote effects: reads only.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Draft opened; envelope `status: "opened"`. |
| `1` | Refused: provider unsupported or unconfigured, pending work, missing or stale metadata, Bench or baseline changed, binding/identity mismatch, draft CAS conflict, cancellation, or an `indeterminate` post-commit origin fence (`committed: true`; reopen and inspect, do not retry). |
| `2` | Usage: non-positive ID, missing Bench, or an unsupported output format. |

With JSON output the editing envelope is written to stdout and a formatted
error to stderr; refusals carry structured `error.code`, `message` and
`recovery`. Configuration failures before command construction keep the
existing CLI error path and may have no envelope: treat missing stdout as an
unknown outcome, never as proof of rollback.

## See also

- [`twig draft stage`](draft-stage.md), [`twig draft retain`](draft-retain.md),
  [`twig draft show`](draft-show.md)
