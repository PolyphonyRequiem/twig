> 🔴 **SUPERSEDED — the board is authoritative.**
> This map was published to Azure DevOps on 2026-08-11 as **#218**, under **#217**, with its
> five tickets as **#219–#223** and the blocking edges wired as real Predecessor links.
> **Do not edit or re-sync this file.** It is kept for the git history of how the map was
> charted. Decisions live on the board; the evidence under `assets/` stays here and is
> still live.
>
> | file | work item |
> |---|---|
> | `map.md` | [#218](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/218) |
> | `tickets/0001-…` | [#219](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/219) (Done) |
> | `tickets/0002-…` | [#220](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/220) |
> | `tickets/0003-…` | [#221](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/221) |
> | `tickets/0004-…` | [#222](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/222) |
> | `tickets/0005-…` | [#223](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/223) |

# Process descriptor — Wayfinder map

## Destination

A build-ready description of what `twig process` should emit as a **process descriptor**, and
how a caller reaches one, with the public-promise questions settled. The map ends at the
implementation handoff; writing the code and shipping it belong to the build that follows.

## Lineage

- **GitHub issue #368** (open, public record) is the source report.
- **ADO 217 — `twig process <type>` output is not a process descriptor** is the work tracker
  for the descriptor; this map is chartered under it and 217 becomes the map's parent.
- **ADO 216 — `--org`/`--project` overrides** is a sibling item with **no dependency edge**
  either way, and is deliberately **not** in this map. Verified during triage: every endpoint
  the descriptor needs is reachable through `AdoIterationService`'s existing `_orgUrl`/`_project`,
  so descriptor depth never forces the override, and the override is useful immediately against
  today's thin output.
- **Hostable work-item detail projection** (`wayfinder-detail-projection/map.md`) is settled
  input for one thing only: it promoted `FormLayout` and its four levels `internal`→`public`
  and established SemVer over `PublicAPI.Shipped.txt` as the compatibility mechanism. This map
  does not reopen that.

## Notes

- Domain vocabulary: `CONTEXT.md` is authoritative for names.
- Grounded at `0b9c2dba` (branch `feat/182-editing-capability-types`, clean, tree matches
  `origin/main` for every file cited below). Every claim in "Grounded facts" was read from
  source or run live in this workspace — none is taken from the report.
- Skills: `wayfinder`, `grilling`, `decision-mapping`, `capability-claim-verification`,
  `drift-checked-documentation`.
- Toolchain, non-negotiable: `export DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5` and
  `export PATH=$DOTNET_ROOT:$PATH`. Verdicts come only from `tools/run-tests.sh`, grepping
  `TWIG-VERDICT`. Never grep `Passed!`.
- One ticket per session.

## Grounded facts (charting input, not decisions)

These replace the report's table. Verified, with sites.

**Emitted today.** `twig process <type> -o json` returns exactly four keys — `type`, `states`,
`fields`, `transitions` — and each field carries exactly four attributes
(`ProcessCommand.cs:194-211`).

🔴 **The `fields` list is not scoped to the type at all.** It comes from the project-wide
`{project}/_apis/wit/fields` (`AdoIterationService.FetchFieldDefinitionsAsync`, lines 486-512)
and is identical for every type. Verified live in this workspace: `twig process Task -o json`
and `twig process Map -o json` each return the same 85 fields in the same order. This is the
load-bearing defect — the output is not merely thin, it is **untrue about which fields belong
to the type** — and it should drive the design rather than sit as one row of eight.

**The thinness starts at the model, not the renderer.** `FieldDefinition`
(`src/Twig.Domain/ValueObjects/FieldDefinition.cs`) is a four-member record, and the response
DTO (`Ado/Dtos/AdoFieldResponse.cs`) parses only those four keys. `allowedValues` appears
**nowhere** in the codebase.

**Already fetched, merely not surfaced:**
- **Rules** — `IProcessRuleProvider` on `AdoIterationService` (`:14`, `:194`, `:318-355`) hits
  `/processes/{id}/workItemTypes/{refName}/rules` and models them as
  `ProcessRule`/`RuleCondition`/`RuleAction`. Only `StateTransitionWorkflow` consumes them.
- **Layout** — `IFormLayoutProvider`, same class (`:387-400`), and there is a **shipped command**,
  `twig process layout <type>` (`ProcessLayoutCommand.cs`, `Program.cs:509`), landed in
  `0c6b45f8` (#282/#365). It is on `main` but **not** in the `v0.86.0` tag the reporter used, so
  that row of the report is stale.

Both `ProcessRule` and `ProcessLayoutCommand` are `internal` **deliberately** —
`ProcessLayoutCommand`'s own remarks say `FormLayout`'s shape is still under design and freezing
it into the public surface now would make it harder to correct.

**Genuinely absent, never touched:** `required`, `defaultValue`, resolved picklist values,
behaviors (backlog levels), type `customization`/`inherits`, and type `referenceName`
(resolved transiently inside the rules/layout fetches, then discarded; `ProcessTypeRecord` has
no such member).

**Correction to the report.** It cites twig #339 as directly related to the missing `required`
flag. #339 is **closed and fixed** — `twig new --field` landed in `c7ac0924` (#343) and
`twig seed new --field/--description` in `1ad9723c` (#345). The relationship inverts: a caller
can now *supply* a required field but still cannot *discover* which fields are required. That
is the surviving gap and it strengthens the case for `required` + picklist values here; it does
not reopen #339.

**~~Unverified, carried on the reporter's word.~~ RESOLVED by 0001 — the claim is REFUTED.**
`/_apis/work/processes/{id}/fields?api-version=7.1` does not return `{"count":0}`; it
returns **HTTP 404**, because `7.1` is not a valid api-version on that route. At
`7.1-preview.1` the endpoint returns all 93 fields including 13 `Custom.*` for this
workspace's own inherited process. See
[0001](tickets/0001-what-the-endpoints-actually-return.md).

## Decisions so far

<!-- one line per closed ticket -->

- [What the process endpoints actually return](tickets/0001-what-the-endpoints-actually-return.md) —
  the reporter's `count: 0` **refuted**: `/processes/{id}/fields?api-version=7.1` *404s*
  (invalid version); at `7.1-preview.1` it returns all 93 fields incl. 13 `Custom.*`, so
  neither endpoint is broken and field enumeration is a free design choice. The real trap
  is that **api-version changes the response SCHEMA** (`required`/`defaultValue` exist only
  at `7.1-preview.2`), `required` lies unless merged with `/rules`, no endpoint links a
  picklist to its field (→ 0005), behaviors are per-process *and* per-type, and cost is
  round-trip-bound: ~4–6 calls/~3 s for one type, ~32 calls/~15 s for all 14.

## Not yet specified

- Whether the per-type behavior *membership* edge belongs in a per-type descriptor, and the
  process-level behavior *catalogue* only in a whole-process one. 0001 settled the wire
  question — they are both, at two routes, and cost one call either way via
  `$expand=behaviors` — so what remains is purely a shape call for 0002/0003.
- Whether the descriptor should ever be **cached** the way process types are today
  (`SqliteProcessTypeStore`), or stay a live fetch. Hangs on 0004's volume answer.
- Whether `twig process layout` survives as its own command once a descriptor exists, or
  becomes a view onto it. Hangs on 0002.

## Newly ticketed by 0001

- [Can a picklist be associated with its field at all](tickets/0005-picklist-field-association.md) —
  no endpoint exposes the link; blocks any promise of resolved picklist values in 0002/0003.

## Out of scope

- `--org`/`--project` overrides. Tracked as **ADO 216**, independently shippable, no dependency
  edge. Verified orthogonal during triage — see Lineage.
- Reopening `#339`. Closed and fixed.
- Writing the descriptor implementation. That is the handoff after this map closes.
- Any change to how work items themselves are read or written.
