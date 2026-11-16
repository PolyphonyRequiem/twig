# Twig - Copilot instructions

## Current product

Twig is a NativeAOT/full-trim .NET 11 RC3 CLI for explicit work-item reads and
changes. The Windows native daemon and public RenderTree SDK are distinct
supported surfaces. RenderTree retains .NET 10/.NET 11 consumers; the
`Twig.DetailHost` sample targets .NET 10.

- CLI: ConsoleAppFramework source generation, static Spectre.Console rendering,
  invariant globalization and source-generated JSON; reflection serialization
  is disabled.
- Persistence: current Core SQLite store, normally `.twig/core.db`, with WAL.
  Existing developer-local data is disposable; there is no migration/adoption
  compatibility requirement.
- Internal semantics/composition: Core, store, provider projects and native
  Infrastructure. Infrastructure is nonpackable, not an external SDK.
- No hardcoded process types/states/fields/templates. Discover vocabulary,
  transitions, requirements and allowed values through the bound provider schema.
- Warnings are errors; nullable is enabled. Prefer sealed classes/records and
  primary constructors, following existing DI modules.
- New serializable types use the owning source-generated JSON context.
- Tests use xUnit, Shouldly and NSubstitute; mirror the source structure.

The [current composition contract](../docs/architecture/clean-core-contract.md)
and executable help define supported routes. Retired standalone MCP/TUI/Herdr,
OMP/item-companion adapters, captured UI envelopes, `plan`/bare-area aliases and
inert presentation flags are not compatibility APIs. The Windows daemon's
read-only `mcp-read-v1` is current and deliberately has no Save/Apply tools.

## Targeting and authority

Structured output (`json`, `json-full`, `json-compact`, `minimal`, `ids`) marks
a machine invocation. Single-item mutations require `--id`; structured
`show`/targeted `tree` reads require a positional ID or `--id`. Preserve explicit
local-ID/list/file scopes where supported. Conflicting targets refuse.

Structured `states` is refused; use `process <discovered-type>`. Machine workspace
output retains explicit Bench scope and local drafts, excluding active-context
relationships. `set`, navigation and seed publication are refused in structured
mode. Structured seed creation chooses `--parent` or `--no-parent`.

No-marker data calls require fresh init, not an old cache/writer graph. Recorded
status is not fresh authenticated access. `connection writes` is inspect-only
correlated transport evidence; the Core v3 proposal journal is execution
authority. Preserve original digests/attempts and never replay uncertain writes.

## Telemetry and privacy

Optional telemetry uses `TWIG_TELEMETRY_ENDPOINT`. These constraints apply to all
collection/emission/transmission:

**Never send, even hashed:** organization/project/team names; user names/emails;
process/template/type names; field/reference names; area/iteration paths;
work-item IDs/titles/descriptions/content; repository/branch names or commit
hashes; any other ADO-specific identifier or process-specific data.

**Safe:** command name, duration, exit code, output format, Twig version, OS,
generic booleans and numeric counts without identifiers.

Property keys pass an allowlist in tests. Reject keys containing `org`,
`project`, `user`, `type`, `name`, `path`, `template`, `field`, `title`, `area`,
`iteration` or `repo`. Zero network calls when the endpoint is unset. Telemetry
failure never changes command execution or exit status.

## Work tracking

Development is tracked through the Twig CLI on ADO
`PolyphonyRequiem/Twig`; GitHub is the public record. Verify connection and
authority rather than inferring a tracker from Git remotes. Do not write work
items without task authorization.

The old plan seeder, SQL/config context-switch scripts and dormant workflow
recipes are retired. Do not copy legacy `.twig` data into a fresh worktree or
revive old scripts to satisfy historical plans.

When authorized to create work, use current seed/proposal commands with
discovered types, assignment fields and explicit scope. Assign to the intended
user and supply a rich description in the field's supported format (Markdown
for supported `System.Description` updates). Explain what/why/context and
acceptance criteria in a readable 2-3 paragraphs unless truly trivial.

For mapped tracked work, include `AB#<issue-id>` in implementing commits; without
a current map, do not invent an ID. Follow
`.github/instructions/pr-grouping.instructions.md` when authoring plan groups.

## Tracked lifecycle

Before implementing an authorized tracked task, establish explicit target,
discover the actual in-progress state, transition through the current CLI and
record a starting note. Add notes at meaningful discovery/change/test/blocker
checkpoints, not only completion.

For completion:

1. Record the final implementation note.
2. Discover the exact type's Done-category state and all required fields with no
   supplying rule using `process description`; do not hardcode a gate table.
3. Author **one** current v3 batch operation carrying the state plus all gate
   fields together. A bare state-only command cannot carry missing fields.
4. Validate/preview that explicit file and apply its exact digest with
   `twig proposal apply --file <path> --confirm <digest>`.
5. Inspect the Core journal and refresh-read the affected item. Only then mark
   its todo complete. Leave human-gated or unresolved work open explicitly.

Do not treat display, cached state, local reset, transport receipt or command
success alone as proof of closure. Multi-operation apply is not globally atomic.
Preserve uncertain attempts; never replay a possibly successful creation or
verified prefix.

For explicitly authorized final delivery, push only when requested, close any
authorized parent through the same gate/digest/journal path, then summarize.
Respect an instruction to commit without pushing.

## Validation and documentation

Read `AGENTS.md` for current bounded serial build/focused-regression/smoke gates.
No automatic broad suites, deadline expansion, watchdog weakening, global tool
repair or compatibility work for disposable local data.

Update canonical command Markdown/index with behavior changes. Declarations own
syntax/defaults; bundled Markdown owns current behavior/examples. Historical
ADRs, Wayfinder, plans, specs and research remain archived evidence, not current
installation/SDK/command contracts.
