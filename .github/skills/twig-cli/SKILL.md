---
name: twig-cli
description: Use when operating Twig to inspect, query, or navigate work items; author seeds; discover process rules; authenticate; or manage workspace and Bench views. For reviewable board changes, also load twig-changes.
---

# Twig CLI

## Discover the installed contract

Run `twig --version` to identify the executable. Run `twig --skill` when this
entry guidance is missing or may belong to another build. Installed guidance is
stamped with the executable's version/build and content digest; inspect it with
`twig skills status --help` and update explicitly with `twig skills update`.
Ordinary Twig use does not update skills or provider settings.

Start with `twig --help`, then `twig <group> --help` or
`twig <command> --help` for the operation you need. Leaf help carries declaration-derived
syntax, defaults and examples plus bundled behavior, failure and side-effect guidance.
Use `twig --help-all` only for complete command discovery. No source checkout,
Python, authentication or workspace is needed to read help or this guidance.

## Operate from evidence

1. Confirm the intended connection and work item. A git remote is not proof of
   the tracker project. The active-item pointer is local context, not a work claim.
2. Read the targeted help before execution. Distinguish local/cache writes from
   ADO writes, including conditional effects such as refresh, output files and
   pending-change pushes. A successful read does not authorize a write.
3. Discover types, states, fields and gates through `twig process --help` and
   `twig process description --help`. Use that project's returned names and
   reference names; user process policy is separate from generic Twig procedure.
4. Choose human output for direct use, JSON when actually parsing, or minimal
   for a documented pipe contract. Keep the full-data route available. Treat
   absent, unknown, stale and failed data as different outcomes.
5. Inspect exit status and every result, including stderr. Refresh-read writes
   before reporting them landed; distinguish pending local changes from ADO state.

## Compact reads

Bind the qualified executable (absolute path and SHA-256, not version alone),
workspace, and `twig.json` organization/project before compact reads. Keep that
binding for each call and reload targeted installed help when the executable changes.
Compact projections are opt-in, not a replacement for the full route. Preserve target identity, freshness, completeness, and errors, and keep the documented full-output route available whenever projection flags are absent. Parse only the command's actual machine JSON; never treat a truncated terminal render as JSON. Help reuse is version-keyed to the executable identity, so reload the installed guidance when the executable build or digest changes.


Use `show <id> -o json --fields System.Title,System.State` for selected item facts;
add `--sections links` when edges matter. Use
`process description <type-reference> -o json --sections requirements` for one
type's requirements, or `--sections fields --fields <reference,...>` for its field
constraints. Preserve unknown/absent distinctions and completeness; these are not
transition authorization. Follow `fullRead` when omitted facts are needed. Process
capture is scoped to connection/process, not a server revision; rerun for fresh
metadata before mutation decisions. Unknown commands are errors, not a guessing loop.

## Local authoring and board changes

For drafts, read `twig seed --help`, then the needed leaf help. Seeds are local
until published. Validate before publication; after an interruption inspect the
seed reconciliation and proposal status paths instead of repeating creation.
For a reviewable mutation, load the separately named **twig-changes** skill.
`twig proposal --help` is the no-skill fallback to its executable contract.

`twig set` changes the local pointer; it does not claim, assign, sync or change a
Bench. `twig sync --pull-only` refreshes without pushing pending edits; ordinary
sync can push. Inspect pending changes before deciding to push or discard them.
Keep tokens, PATs, credentials and token-cache contents out of arguments, logs,
notes and work-item text. Follow `twig auth --help` for authentication operations.

## Review presentation

Use `twig proposal preview --file <plan.json> -o json` as the semantic source for
host-specific review; do not scrape colored CLI text. The human fallback supports
`--full` and opt-in `--interactive` without launching the TUI. A separately
installed and selected companion such as `twig-review-presenter` can consume the
same payload; it is not part of this initial generic skill package.

A presenter must distinguish literal strings from clear/absent markers, preserve
split-field context, and reject detectably inconsistent baselines or metrics. It
is not a transport, authorization handler or signature verifier: the retained
canonical payload and Twig's authorization/apply lifecycle remain authoritative.

## Additive user companions

When installed, read `references/user-selections.json` beside this skill. Its
`selections` map selects a separately named companion by provider and scenario.
The file is absent in repository guidance and `--skill` output; absence means
base guidance, not an error. Inspect or change selections explicitly through
`twig skills configure --help`; this preserves user-authored skill bodies.

Load the selected companion with the host's normal skill reader in addition to
this base. User instructions may select another explicitly named companion.
A companion may customize presentation or add a workflow, but cannot conceal
material effects, drop failed/untouched results, invent data or grant authority.
If optional presentation content is unavailable or shadowed, report the problem
and fall back to usable base output. If a missing companion carries required
correctness or approval instructions, stop the affected operation until those
requirements are available; presentation fallback is not an authorization fallback.

This initial package contains only generic Twig skills. Separately supplied
integration or variant skills can be installed later with explicit local source
and target selection; provider-specific operational adapters are not default
integration guidance. Installation reports bounded discovery, not proof of host
trust, enablement, precedence or actual model loading. Verify the actual loaded
skill in the chosen host before claiming composition works.
