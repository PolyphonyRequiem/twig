# `twig draft stage`

Stage an exact, ordered field batch into an already opened recoverable native
draft. The request is one strict version 1 JSON document read from stdin.

```bash
twig draft stage --stdin -o json < stage.json
```

## Flags

| Flag | Contract |
|---|---|
| `--stdin` | Required. Read one request and wait for EOF before staging. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

The request contains `version: 1`, the **unchanged** `nativeContext` from
OPEN, decimal-string `generation`, `baselineRevision` and `observedRevision`,
and 1-128 ordered `changes` of `{ "fieldRef": "...", "proposedValue": ... }`
with a string or `null` value. `null` and `""` remain distinct. Input is limited
to 262144 UTF-8 bytes and JSON depth 32; unknown or duplicate members are
refused; exceeding a limit is an error, never truncation.

Native code re-admits the explicit Bench, rereads the served evidence, refuses
pending local work, and requires the exact draft generation and revisions. Each
field must be editable on the served form with a carried baseline value, and
the whole resulting intent (including carried state gates) is validated before
any write. The draft is then saved by compare-and-swap inside one local SQLite
transaction.

STAGE writes only the recoverable draft (`mutates: local`). It never writes
pending changes, a proposal journal or the server, and never rebases intent.
Evidence and drafts are in separate attached WAL files: normal commit/rollback
is shared, but cross-file crash or power-loss atomicity is **not** guaranteed.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Staged; envelope `status: "staged"`. |
| `1` | Refused: authority invalidated, item unavailable, pending work, draft unavailable or CAS conflict, revision conflict, duplicate or non-editable field, generation exhausted, cancellation, or an `indeterminate` post-commit origin fence (`committed: true`; reopen and inspect, do not retry). |
| `2` | Usage: missing `--stdin`, invalid request shape, `invalid-json`, `input-too-large`, or an unsupported output format. |

JSON refusals write the envelope to stdout and a formatted error to stderr.
Missing or invalid stdout is an unknown outcome, not proof of rollback.

## See also

- [`twig draft open`](draft-open.md), [`twig draft retain`](draft-retain.md)
