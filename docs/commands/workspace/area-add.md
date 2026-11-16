---
command: workspace area add
group: workspace
summary: Add or update an area condition within one shared native planning rule.
stability: stable
mutates: local
---

# `twig workspace area add`

```sh
twig workspace area add "Contoso\Team A" --exact
```

## Behavior

Edits one current-connection planning rule in the selected shared Bench.
Default matches descendants; `--exact` narrows to the exact path.
Existing same-path intent is updated rather than duplicated. It does not write
old `twig.json` area arrays.

With multiple planning rules, use `--rule <one-based-index>` and
`--expect-revision <revision>` from a fresh `bench rules` listing.
Other area choices are OR; area/iteration/ownership dimensions inside the
same rule are AND. Independent selectors are OR.

## Effects

Shared local intent under revision/native admission checks, never provider item
publication. GitHub planning edits are not qualified.

## Failure modes

Invalid path, missing selection, ambiguous rule, noncanonical shape or changed
revision fails without combining/broadening unrelated rules.
See [planning](../bench/planning.md).
