---
command: show-batch
group: context
summary: Read explicit cached numeric targets and disclose missing items.
stability: stable
mutates: none
---

# `twig show-batch`

```sh
twig show-batch 12345,12346 -o json
twig show-batch --batch 12345,12346 -o json --fields System.Title
```

## Behavior

Reads positive native IDs from the Core snapshot without provider HTTP.
Missing IDs are disclosed in `missingIds` and cause a nonzero exit even when
other rows are returned. Fetch each missing item with `twig show <id> --refresh`.
There is no silent network fallback.

`--fields` filters retained fields. `--sections` is currently refused.
`--include-fields` requires exactly `-o json`, distinct IDs, and no field/section
projection; it does not restore the retired target-export envelope.

## Effects

Cached inspection only; no draft flush, context switch, or publication.

## Failure modes

Empty/malformed/nonpositive IDs, incompatible projections, unavailable store,
and missing targets fail. Do not treat a partially populated response as a
complete successful read.
