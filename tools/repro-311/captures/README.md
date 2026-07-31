# Captured #311 aborts

`artifacts/` is gitignored, so a capture that is worth reasoning about later gets
copied here. Each capture is a pair:

- `*.console.txt` — the `dotnet test` console output, containing the abort banner
  and the false-green summary line. (`.txt`, not `.log`: `.gitignore` carries a
  blanket `*.log` rule that silently swallows the file otherwise.)
- `*.trace.tsv` — the boundary trace written by `TestProgressTrace`
  (`TWIG_TEST_TRACE`), one flushed START/END line per test.

## Reconciling a trace

The question a capture answers is *was any test in flight when the run aborted?*

```bash
awk -F'\t' '{if($2=="START")s[$3]++; else if($2=="END")e[$3]++}
  END{for(k in s) if(!(k in e)) print "IN-FLIGHT: "k}' <trace.tsv>
```

Empty output means nothing was in flight — the runner stopped dispatching rather
than getting stuck inside a test. All four captures to date report empty.

## Index

| Capture | Date | Conditions | Shape |
|---|---|---|---|
| `2026-07-30-loaded-ab-attempt1-OFF` | 2026-07-30 | heavy load, `--diag` off, attempt 1/40 of the loaded A/B | 1092 STARTs / 1092 ENDs, nothing in flight, 7.3 s of test-body time in a 310 s run |

See the `#311` sections of `AGENTS.md` for the full hit-rate table and the
hypotheses that have been killed.
