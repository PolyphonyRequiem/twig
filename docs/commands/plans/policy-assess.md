# `twig policy assess`

Assess cached compliance of explicitly named published items against the
repository's declared policy (`twig.policy.json`), and list bounded repair
alternatives. This is current-state assessment, not proposal enforcement.

```text
twig policy assess --ids 42,43
twig policy assess --ids 42,43 -o json
```

## Flags

| Flag | Contract |
|---|---|
| `--ids` | Required 1-200 comma-separated positive work-item IDs. Duplicates are collapsed. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

Assessment loads the declared policy and evaluates only cached evidence for the
named scope, plus incident cached dependency sources in both directions. It
reports typed findings, an evidence digest, unavailable checks and up to a
bounded number of explicit strategies per finding. It is not a live or
whole-board certification: contradictory reciprocal observations stay
unavailable, and parent checks do not certify out-of-scope children.

It performs no refresh and no writes to the cache, journal or ADO
(`mutates: none`). A missing or empty policy refuses rather than claiming
compliance. Minimal output is one tab-separated row of status, evidence digest,
scope, `cached` and dependency scope, followed by finding rows.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Complete, compliant observation for the named scope. |
| `1` | Noncompliance or unavailable evidence, or a refusal (no policy declared, policy without rules, unreadable policy, I/O failure). |
| `2` | Usage: invalid `--ids` or an unsupported output format. |

## See also

- [`twig policy reconcile`](policy-reconcile.md),
  [policy contract](../../specs/declared-policy-decisions.spec.md#current-state-assessment-and-reconciliation)
