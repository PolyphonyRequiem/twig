# Twig service engineering policy

Use the rows that match the diff. Apply this policy to new or changed service
code; do not broaden it into unrelated migrations.

| ID | Trigger | Rule | Review evidence |
|---|---|---|---|
| 01 | State, outcome, variant or transition | Model exclusive cases with named alternatives and explicit exhaustive matches where supported. Consider native `union` or `closed` on supported targets, not as wire types. Check default values; avoid blanket discard arms. | Changed cases, consumers and transition/default tests. |
| 02 | Message, input, decoder or mapping | Validate shape and invariants at ingress; map to domain values. Deserialization, `required` and nullable annotations are not domain validation. | Ingress, validator and relevant malformed/unsupported input tests. |
| 03 | Sparse lookup, foreign API or nullability | Keep nullable analysis on and owned domain reference contracts non-null. Use explicit outcomes for meaningful absence; use honest nullable contracts where compatibility requires and normalize at the boundary. Review `!` and suppressions. | Compatibility rationale, normalization and null/default tests where applicable. |
| 04 | Build, analysis, test or diagnostic config | Preserve blocking nullable and exhaustiveness diagnostics. The policy PR workflow builds and runs the catalog invariant suite; other applicable checks remain necessary. | Build and test results, or the exact blocker. |
| 05 | Project scope, framework or tooling change | Preserve the existing runtime and scope. No platform upgrade or blanket rewrite is required. | Projects and dependencies included; deferred scope identified. |

The compiler enforces its diagnostics, not the domain's no-nullable-reference
design rule, the accuracy of compatibility annotations, or coverage of every
ingress. Review those explicitly. A default-initialized union or struct may
bypass normal initialization; test or prevent that path when it matters.
