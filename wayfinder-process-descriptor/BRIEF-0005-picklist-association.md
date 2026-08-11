# Brief — ticket 0005, picklist ↔ field association

You are resolving **one** Wayfinder ticket, then stopping. This is research, AFK. No human
gates are expected; drive it alone.

## Read these first, in this order

1. **`AGENTS.md`** at the repo root — build/test rules and, critically, the
   "Where work is tracked" section. Non-obvious and authoritative.
2. **Work item #218** — the map. `twig show 218 --output json --refresh`.
   Its description carries the destination, the dependency order, and the grounded facts.
3. **Work item #223** — your ticket. `twig show 223 --output json --refresh`.
4. **Work item #219** — the closed predecessor. Read its `Custom.WayfinderAnswer` field,
   not just its description; the answer is where the useful material is.
5. **`wayfinder-process-descriptor/assets/0001-endpoint-findings.md`** — the full evidence
   behind #219, including every endpoint already probed and found empty.
6. Load the `wayfinder` skill and follow it. Load `azure-devops-work-tracking` for REST
   mechanics and auth.

## 🔴 The board is authoritative, not the markdown

The map was published to ADO on 2026-08-11. The files under
`wayfinder-process-descriptor/tickets/` carry **SUPERSEDED** banners and exist only for git
history. **Record your answer on work item #223**, not in the markdown. Do not re-sync the
files.

## Claim the ticket first

Before any work: `twig state Doing --id 223`. This is the board equivalent of the
wayfinder skill's claim rule, so a concurrent session skips it.

## What this ticket is

0001 established that **no ADO endpoint associates a picklist with the field it backs.**
Already checked and found empty — do not redo these:

- process-wide `/fields` at every working api-version
- per-type `/fields`, with and without `$expand=all`
- the form `layout`
- `/_apis/wit/fields/{ref}` (`isPicklist: false`, `picklistId: null` at 7.1, 7.1-preview.2,
  7.1-preview.3, 7.2-preview.3, 6.0)
- project-scoped `/{project}/_apis/wit/workitemtypes/{type}/fields/{ref}?$expand=all`
  (`allowedValues: []`)

**Not yet exhausted**, and where your effort should go:

- the process **export/import** payload (`/_apis/work/processes/{id}` with export expansions)
- the `xmlForm` on the classic type — checked on exactly one type, no `ALLOWEDVALUES` found;
  check more, especially a type whose picklist field is on the form
- the WIT **field usage / picklist admin** routes
- whether the association is only knowable to whoever created the picklist

If no endpoint carries it, the ticket still closes — decide what is **honest to emit**.
Options are in the ticket body. 🔴 Do not settle this with a silent name-matching heuristic;
that is the same failure class as the current project-wide field list, which is untrue about
which fields belong to the type.

## Fixtures already to hand

- Inherited process **Niflheim** `7f984e4c-e856-4fc3-8457-fd4e8acf2e57` — what the **Twig**
  project actually runs on. 🔴 *Not* the process named "Twig", which has zero projects.
- Stock **Basic** `b8a3a935-7e91-48b8-a94c-606d37c3e9f2`.
- Seven org-wide picklists, values already captured under `assets/raw/Niflheim-list-*.json`.
- Reusable probes: `assets/probe.sh <name> '<path-after-org>'` and `assets/probe-all.py`.

## Auth

`export AZURE_CONFIG_DIR=/home/polyphonyrequiem/.azure` — the login lives in the **login**
home, not the Hermes profile home. Without it you read a stale or empty cache.

## Toolchain, if you build or test anything

```
export DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5
export PATH=$DOTNET_ROOT:$PATH
```

Both, or every suite exits 145 with bogus FAILED verdicts. Verdicts come only from
`tools/run-tests.sh`, grepping `TWIG-VERDICT`. Never grep `Passed!`.

## Git

Branch `docs/process-descriptor-map` holds this map's evidence and is pushed. If you add
evidence, commit it there. Do **not** commit onto `feat/182-editing-capability-types` —
unrelated in-flight work.

## When done

1. Write the answer into **#223**'s `Custom.WayfinderAnswer` field.
2. `twig state Done --id 223`. The type has a rule requiring the answer at Done, so a
   failure there means the field did not land.
3. Append a one-line gist to **#218**'s `Custom.WayfinderDecisionsSoFar` — read the existing
   value first and re-send it whole; that field is not append-only.
4. Save large captured payloads under `assets/` and reference them; do not paste them into
   the work item.
5. If your answer sharpens fog in #218 into a real question, create the ticket and wire it:
   `twig link predecessor <blocker> --id <blocked>` works as of 0.86.1.

## Scope

Planning, not building. One ticket per session — resolve 0005 and stop. Do not also start
0002. GitHub issue #368 stays **open** as the public record; do not close it.
