# Harness run log — reference profile 1.0.0

Executed 2026-08-31 against `dev.azure.com/PolyphonyRequiem` / `Twig-Reference-Sandbox`
under AB#847. Every mutation below went through the native Twig proposal surface
(`twig proposal validate` → `preview` → `apply --confirm <digest> --authorize <identity>`)
except the one step the native contract cannot express, which is called out explicitly.

Binary: `twig 0.91.6-alpha.0.8`.
Sandbox workspace: `~/.twig/harness/twig-reference/sandbox-ws` (outside the Twig repo;
`twig init` refuses a non-git root, so the workspace root is its own local git repo).
Proposal directory: `.twig/ado-plans/15dd243d-8ac3-4335-84f0-5507c4603dc5/`.

## Steps 2-9 — human ADO tailoring: already done, re-verified live

Not re-executed. Re-verified against live ADO at run time rather than trusted from the
prior note:

| Check | Result |
|---|---|
| Process `Twig-Reference` `a0afde20-50eb-4e30-b442-c9e7f13e752a` inherits Basic `b8a3a935-…` | PASS |
| `Epic` and `Issue` `isDisabled: true` | PASS |
| `Initiative` / `Investigation` / `Feature` / `Bug` present, `customization: custom` | PASS |
| Backlog behaviors: Initiative→`Microsoft.VSTS.Basic.EpicBacklogBehavior`; Investigation/Feature/Bug→`System.RequirementBacklogBehavior`; Task→`System.TaskBacklogBehavior` | PASS |
| Project `Twig-Reference-Sandbox` `2c534971-1a18-4880-9cce-2ca1fb2c3cd6` `wellFormed` | PASS |
| Backlog topology: Epics={Epic,Initiative}, Issues={Bug,Feature,Investigation,Issue}, Tasks={Task} | PASS |

The disabled types still appear in their backlog's `workItemTypes` list; `isDisabled` on
the process type is what makes `Task` the sole *creatable* sprint-entry type. The prior
run's functional proof (`Epic`/`Issue` creation refused with `VS403074`) stands.

## Step 10 — baseline

`twig process description -o json` → `evidence/00-sandbox-baseline.json` (330,188 bytes;
header pins `processId` and `project`).

## Step 12 — seed the hierarchy

`proposals/1-seed-hierarchy.json`, digest
`7e76135a024956df809cc99c98e1e4206e485666bb243f8ba3b0619271a19e0f`.
Seven `publish-seed` ops, all `Verified` first attempt, no `Failed` row:

| Alias | Type | Published id |
|---|---|---|
| INIT | Initiative | 857 |
| INV | Investigation | 858 |
| FEAT | Feature | 859 |
| BUG | Bug | 860 |
| TA | Task | 861 |
| TB | Task | 862 |
| TC | Task | 863 |

Refreshed read-back confirmed all seven; `twig pending` count 0.

Rank snapshot `evidence/10-rank-before.json` captured here — after the publish, before
any link op.

## Step 13 — link the hierarchy

Split across two proposal files because the native contract permits **at most one op per
`workItemId` per proposal**.

`proposals/2-parent-links.json`, digest
`5911b462f8b5b1330b721e0164680882df237aeacb3159e5c88142364d9e0048` — six `add-link`
`parent` ops, all `Verified`. Expressed **child-side** (`workItemId` = child,
`otherId` = parent); the parent-side spelling in AB#733 §4.2 would have put three ops on
`workItemId` 857 in one file and been refused.

`proposals/3-dependency-and-related.json`, digest
`34677e9a7f189061c298b78a223d9d32289d75c45fccdb330f7b98af62f35098` — `predecessor`
(862←861) and `related` (858↔859), both `Verified`.

### Artifact link — not expressible as a proposal op

`proposals/probe-artifact-link-REJECTED.json` records the attempt. `twig proposal
validate` refuses it:

```
plan.invalid_relation  /operations/0/relation
Relation 'artifact' is not one of parent | predecessor | successor | related.
```

The surface was therefore captured out of band, so it is **recorded as passing with a
documented tooling gap** rather than silently skipped:

1. The Sandbox repo `Twig-Reference-Sandbox` `417bcdda-211f-4611-a8eb-c5afac7e0a26` was
   empty (no default branch), so branch `refs/heads/harness` was created via the Git
   pushes REST route (push 736, commit `f0e0781e`).
2. `ArtifactLink` → `vstfs:///Git/Ref/<projectId>%2F<repoId>%2FGBharness` was added to
   #859 via a `json-patch` PATCH on the work item (rev 1 → 2).

See README "Discrepancies found" item 3.

## Step 14 — evidence capture

`./capture-evidence.sh surfaces` then `./capture-evidence.sh rank after`. Twelve JSON
artifacts under `evidence/`.

## Step 15 — gate

`./gate.sh` → **PASS**, all ten surfaces, exit 0. The gate was negative-tested by
corrupting copies of surfaces 07 and 10 in a scratch directory; it reported both as FAIL
and exited 1, so the pass is a real assertion rather than a rubber stamp.

## Observations recorded during the run

- `twig sync --pull-only` in a workspace bound to `Twig-Reference-Sandbox` (0 work items)
  cached **725 items belonging to other projects** (`Niflheim`, `Twig`, …). The
  underlying shape is the same one seen in raw ADO: a WIQL `select … from workitems` with
  no `System.TeamProject` predicate is org-wide even on a project-scoped URL. This did not
  affect the harness, which addresses fixtures by id, but it is a real defect.
- Adding a relation did **not** bump `System.Rev` on either endpoint (verified directly
  against ADO, not just twig's cache), so `expectedRevision` stayed 1 across the parent
  link ops. The artifact-link PATCH *did* bump it.
- State casing differs by origin exactly as the prior run documented: ADO minted the four
  custom types with `To do`, while the inherited `Task` keeps Basic's `To Do`. Both are
  present in this bundle's evidence.
