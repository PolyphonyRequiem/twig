# `twig policy reconcile`

Render one explicitly selected repair from a current policy assessment as a
native proposal. It authors the proposal only; review, confirmation,
authorization and journaled apply remain separate.

```bash
twig policy reconcile --ids 42,43 --evidence <digest> --finding f1 --strategy reparent --parent 43
twig policy reconcile --ids 42 --evidence <digest> --finding f1 --strategy detach-parent -o json --proposal-only > repair.json
```

## Flags

| Flag | Contract |
|---|---|
| `--ids` | Required 1-200 comma-separated positive IDs; the same scope as the assessment. |
| `--evidence` | Required evidence digest from a current `twig policy assess`. |
| `--finding`, `--strategy` | Required finding and strategy IDs chosen from that assessment. |
| `--parent` | Parent target for parent strategies; never guessed. |
| `--proposal-only` | Boolean. With JSON output, writes only the canonical proposal JSON. Requires `-o json`. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

Reconcile reloads the declared policy, re-evaluates cached evidence and
requires it to match `--evidence`. It projects the selected strategy and
validates the result with the canonical proposal parser. It requires a
configured connection for the proposal workspace. Parent targets and dependency edges
are never inferred. Supported repairs detach or reparent a disallowed parent, or remove
an explicitly selected self-dependency, duplicate dependency or witnessed cycle edge.
Label repair strategies are no longer supported.

A policy file still declaring `label-applicability` fails closed as a policy-load error:
proposals remain blocked until the obsolete rule is deleted. Label/tag policy redesign
and the broader engine definition are deferred to post-1.0 Epic #1216.

The proposal is written only to stdout. Reconcile performs no cache, journal or
ADO writes (`mutates: none`) and creates no proposal file of its own. Shell
redirection such as `> repair.json` is the caller's choice. JSON output
without `--proposal-only` returns `canPropose`, `digest`, the before and
projected assessments, and the proposal. Then use `twig proposal preview` and
`twig proposal apply` with the reviewed digest and explicit authorization;
refresh and reassess after apply, since a successful write alone does not prove
compliance.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | A proposal was rendered, **not** proof of compliance. |
| `1` | The repair could not be proposed (stale or incomplete evidence, unknown finding or strategy, missing parent), or a refusal: no or empty policy, missing connection, invalid generated proposal, I/O failure. |
| `2` | Usage: invalid `--ids`, missing `--evidence`/`--finding`/`--strategy`, `--proposal-only` without JSON, or an unsupported output format. |

## See also

- [`twig policy assess`](policy-assess.md),
  [`twig proposal preview`](proposal-preview.md),
  [`twig proposal apply`](proposal-apply.md)
