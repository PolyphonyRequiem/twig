# Reference-process validation harness — profile 1.0.0

Machine-checkable proof that the Twig reference process behaves as
`docs/projects/reference-process-base-and-harness.plan.md` (AB#733, "T2") specifies, run
against a real ADO project. This bundle is the artifact AB#727 means when it requires the
reference process to be "exercised against a Sandbox project before it is treated as
authoritative".

**Status: PASS** — all ten required surfaces, 2026-08-31, under AB#847.

| Thing | Value |
|---|---|
| Profile version | `1.0.0` (matches `profile.json` `profileVersion`) |
| Organization | `PolyphonyRequiem` |
| Project | `Twig-Reference-Sandbox` (`2c534971-1a18-4880-9cce-2ca1fb2c3cd6`) |
| Process | `Twig-Reference` (`a0afde20-50eb-4e30-b442-c9e7f13e752a`), inherited from Basic |
| Team | `Twig-Reference-Sandbox Team` |
| Sprint iteration | `Twig-Reference-Sandbox\Sprint 1` (`9b7305e2-…`) |
| twig | `0.91.6-alpha.0.8` |

## Layout

```
1.0.0/
├── README.md                    # this file
├── run-log.md                   # what was executed, in order, with digests
├── fixtures.json                # the published fixture ids the scripts key on
├── capture-evidence.sh          # re-capture the surfaces from live ADO
├── gate.sh                      # assert the ten pass criteria (step 15)
├── proposals/                   # the proposal v1 files actually applied
└── evidence/                    # 12 JSON artifacts; JSON is authoritative
```

## Running it

```bash
./gate.sh                        # offline: reads the committed evidence only
./capture-evidence.sh surfaces   # online: re-captures 01-09 from live ADO
./capture-evidence.sh rank after # online: re-captures the rank snapshot
```

`gate.sh` reads nothing but `evidence/` and `fixtures.json`, so it runs in CI or offline.
`capture-evidence.sh` needs `az` logged in against the org.

## The ten surfaces

| # | Surface | Evidence | Result |
|---|---|---|---|
| 01 | `Initiative` on the portfolio backlog | `01-initiative-backlog.json` | PASS |
| 02 | `Investigation` on the Requirements backlog | `02-investigation-work.json` | PASS |
| 03 | `Feature` on the Requirements backlog | `03-feature-work.json` | PASS |
| 04 | `Bug` on the Requirements backlog | `04-bug-work.json` | PASS |
| 05 | `Task` on the sprint board | `05-task-sprint.json` | PASS |
| 06 | Hierarchy renders as decomposition | `06-hierarchy-links.json` | PASS |
| 07 | Predecessor/successor renders as dependency | `07-predecessor-successor.json` | PASS |
| 08 | Related renders nondirectionally | `08-related-links.json` | PASS |
| 09 | Artifact link | `09-artifact-links.json` | PASS (see item 3 below) |
| 10 | Rank preserved across publish + link | `10-rank-{before,after}.json`, `10-rank-diff.txt` | PASS (diff empty) |

Fixture shape:

```
Initiative #857
├── Investigation #858        related ──> Feature #859
├── Feature #859              ArtifactLink ──> GBharness
│   ├── Task #861             predecessor of ──> Task #862
│   ├── Task #862
│   └── Task #863
└── Bug #860
```

The three Tasks are committed to `Sprint 1`; nothing else is. That is the sprint-entry
invariant, enforced structurally by the backlog behaviors rather than by convention.

### Screenshots

None. T2 §4.1 lists optional `.png` companions for the backlog and sprint surfaces, and is
explicit that "the `.json` proof is authoritative — the harness gate reads JSON, not
images". `gate.sh` never opens a PNG, so their absence does not weaken the gate. Capturing
them needs an authenticated browser session against the ADO UI and is left to whoever wants
the human-consumable view.

## Discrepancies found in T2 while executing

These were found by running the recipe and are corrected in the T2 note itself.

1. **Publish-step contradiction (T2 §2.6 vs §3 row 7).** §2.6 correctly states that an
   inherited process has no publish step, while the §3 ordering table still carried a
   human "Publish the process" row. There is no such transition; the row is removed.

2. **Surface-count contradiction (executive summary vs §4.3).** The summary said six
   observation surfaces; §4.3 enumerates ten. Ten is correct and is what this bundle and
   `gate.sh` implement.

3. **§4.2's plan document is not a real proposal file, and one of its ops cannot exist.**
   The illustrative `plan.yaml` uses `ops` / `seed` / `alias` / `from` / `to` / `linkType`.
   The native contract is proposal v1: top-level `version` + `workspace` + `operations`,
   kinds `batch` / `add-link` / `remove-link` / `publish-seed` / `delete`, and a **closed**
   relation set `parent | predecessor | successor | related`. Two consequences:
   - `ArtifactLink` is **not expressible** as a proposal op. `twig proposal validate`
     rejects it with `plan.invalid_relation`; the probe is kept at
     `proposals/probe-artifact-link-REJECTED.json` as evidence. Surface 09 was captured via
     the ADO REST route instead and is logged as a tooling gap, not a silent skip.
   - Parent links must be expressed **child-side**. The native surface allows at most one
     op per `workItemId` per proposal file, and §4.2's parent-side spelling puts three ops
     on the Initiative in a single file.

4. **Rank row 10's capture recipe is unsatisfiable as written.** It says to capture
   "`twig tree @FEAT` immediately after step 12's task publish, again after step 13's link
   ops", and to compare the child order under `@FEAT`. Immediately after step 12 the Feature
   has no children — they are created by step 13 — so the "before" snapshot is necessarily
   empty and the comparison is vacuous. This run captures server-owned **backlog order**
   for the portfolio and requirement backlogs before and after the link ops instead, which
   is what "rank preserved across publish + link" actually means and is a non-vacuous test.

## Gating rule

Per T2 §5 this bundle is a hard gate for **publishing a new version of the reference
profile**, not a runtime gate and not a CI gate on every commit. A `profileVersion` bump
requires a bundle at the new version whose `gate.sh` exits 0. Twig core never reads
`docs/reference-process/harness/` at runtime; it validates the live process through
`IReferenceProfileProvider`.

## Prior art

The step 2-9 human tailoring was performed under AB#727/AB#733 on 2026-08-27 and is *not*
re-executed here — it is re-verified live at the top of `run-log.md`. That run's own
evidence (18 REST responses covering the seven tailoring gates, plus the `VS403074` proof
that `Epic`/`Issue` creation is refused) lives outside the repo at
`~/.twig/harness/twig-reference/`.
