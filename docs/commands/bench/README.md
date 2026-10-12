# `twig bench`

A **Bench** is a named, durable, saved backlog. You name an arrangement of work
once and return to it later; several Benches can exist side by side, and the one
you are currently standing on decides what your workspace view shows. Everything
standing on a Bench sees the same Bench — there are no private pins.

A Bench holds **selectors** (a pin is a selector that matches one item; a query
is a selector that matches a body of work). The store is durable: a Bench and its
selectors survive cache rebuilds, and the default Bench is always present.

The `bench` command group is the shared surface for managing that store — create
a Bench, list what exists, switch which one is current, or delete one you no
longer need. Every subcommand accepts `-o|--output human|json|minimal`; the
format is *declared* by the caller, never sniffed from the terminal, so a
command means the same thing in a pipe as at a prompt.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`bench create`](./create.md)|Create a Bench with a name you will recognise later.|local|
|[`bench list`](./list.md)|List the Benches that exist, marking the current one.|none|
|[`bench switch`](./switch.md)|Stand on another Bench.|local|
|[`bench delete`](./delete.md)|Delete a Bench — one holding pins refuses without `--confirm`.|local|
|`bench detail <id>`|Read full cached work-item or seed detail without changing context.|none|
|`bench configuration area candidates`|Read official configured-team paths and Exact/Under semantics.|none|
|`bench browser`|Opt-in shared native Bench terminal, local editing and read-only Review; stable TUI defaults remain unchanged.|local only; explicit S pulls selected root/links|

Listing, creating a named Bench and switching read existing arrangements as stored
metadata, without identity-profile discovery or query evaluation. First-use default
initialization still validates the admitted canonical identity; a refusal creates
nothing. Native connection admission, captured target IDs and atomic storage guards
remain in force. Creating a Bench does not select it. Metadata reads never rewrite
saved selectors, and default self membership is still rebound to the current
canonical principal whenever a workspace, pin or query consumer evaluates it.

## Behavior

### Read full work-item detail

```powershell
twig bench detail 42
twig bench detail 42 --sync            # explicitly pull only this item and its links
twig bench detail -1 --width 100 -o json --expect-bench 7
```

Detail opens from cache using the same renderer as `twig show`, with full values
rather than preview truncation. Opening, resizing, and refreshing cached detail never
call `twig set`, discover a remote identity, or refresh work items. The presentation
includes cached parent/children, links, freshness and pending-edit indicators. The
standard description renders as rich HTML even without cached field metadata;
metadata identifies other HTML fields. Headings, lists, code, links and tables remain
scrollable without a line cap; unknown field types remain literal. Scripts, styles,
remote images and source terminal-control sequences are never executed.

In the browser, **Enter** expands this status beneath the selected Bench row in an
open-right frame: square left corners, a left edge and horizontal rules, with no
right border. The tree/table rows and expanded detail form one continuous scrolling
surface; ancestors and following work items remain in that same sequence. **Enter**
or **Esc** collapses the detail and restores the previous Bench selection, folds and
viewport. CLI detail returns the same open-right status frame without a duplicate
summary line; ordinary `twig show` retains its existing presentation.

In the browser, **S** explicitly pulls only the captured positive item and its links;
**R** reloads its cached presentation. `--sync` is the CLI equivalent of S. Neither
action changes the active work item, flushes pending edits, or fetches related targets.
Local edits remain protected and seeds remain unpublished: seed sync is refused.
While an action is busy, repeated actions are refused; failures retain readable
cached detail and its composed scroll position.

JSON carries `version: 1`, captured Bench/binding/identity IDs, `workItemId`,
`title`, and complete renderer-generated `ansi`. The optional `--expect-bench`,
`--expect-binding`, and `--expect-identity` refuse retargeting. Minimal output is
complete unstyled detail; `ids` is not meaningful for this single-item view.

### Opt-in Native Bench Browser

`twig bench browser --cwd <workspace> --view tree` opens the shared native terminal
application; `--view table` selects Table. It uses admitted native modules in-process,
not a Go UI or read subprocess. This candidate does not redirect `twig tui`,
`twig-tui`, the stable standalone Go browser, or the installed Herdr plugin.

- **Browse:** 1 Tree, 2 Table, arrows/j/k select locally; Left/Right/Space fold;
  Enter opens or closes full inline Show beneath all wrapped lines of the item.
  Mouse disclosure/click/double-click and wheel use the same continuous surface.
  Page/Home/End scroll; resize reflows retained data. Selection never calls `twig set`.
- **Read:** R is cache-only. S explicitly pulls the captured positive item and links,
  never pushes pending work or fetches unrelated targets. Seed S refuses. Retryable
  same-origin failures retain content; authority changes clear visible/hidden state
  and stop operations until explicit Ctrl+R reconnect creates a fresh native runtime.
- **Configure:** c opens Pins/Areas/Sprints/Benches; digits or Tab select a section.
  Pins use independent p/P Single/Subtree toggles; a/A accepts manual IDs, including
  uncached IDs shown as unverified. Areas use a/d, x Exact/u Under, and an explicit t
  configured-team candidate read. Sprints use a/d and native expressions. Benches use
  n create, Enter switch, and d guarded delete; Default is protected and stale contents
  require a new confirmation. These operations preserve pending work.
- **Edit:** E opens the served document, including server header controls before pages.
  Typing is limited by the shared local sink (Title/State/Assigned To), not server
  read-only metadata. Enter edits; Tab accepts/moves; Ctrl+S stages one local batch.
  Unsupported/missing values or invalid transitions refuse; failed saves retain input.
  Esc returns without discarding; Ctrl+D explicitly discards only unsaved input.
  Saved pending values survive viewer restart. No UI action publishes them.
- **Review:** 3 opens the latest qualified unresolved native proposal, or start with
  `--file <proposal>` to capture one explicit file. D details, B brief and Esc/c back
  remain read-only. Original digest/file/journal origin are rechecked; a changed
  proposal clears old content rather than silently substituting a replacement.

The footer stays on the last visible row and clips horizontally. q quits when no
unsaved buffer needs a decision. Modes, inherited code pages and POSIX termios are
restored on exit. Native terminal code targets win-x64/linux-x64/osx-arm64; source or
an unsigned smoke build is not signed installed-platform qualification.

`--herdr --intent open|status|tree|table|view|review|exit-review|reconnect|close`
routes authenticated bounded controls to the same application in an opt-in native
manifest-owned pane. The isolated manifest is `integrations/herdr-aot/herdr-plugin.toml`
(`twig-herdr-aot-candidate`); evaluation configs belong only to a separate named
session, never the normal config. Installed protocol22 was the measured capability
contract: plugin-pane open/close, opaque owner metadata, pane get/list/process-info,
captured source topology and no-focus launch. Display IDs alone are not authority.
Both manifest pane entries launch the native image directly, including Windows;
the exact reported native root PID (or a positive foreground PID) is required.
Control protocol 3 uses short unpredictable pipe identifiers within standard macOS temp-path
limits and acknowledges a nonce/owner/source-validated complete reply before disconnect
or close delivery. Version-1/2 registrations refuse rather than being adopted; recovery
requires the same control/store contract. Authentication tokens and nonces remain 256-bit.
An uncatchable kill may leave a stale private registration; explicit proved-dead
owner recovery never steals a slow live peer. Supported legacy migration requires
explicitly closing the candidate host; no native quiescence exemption is invented.

Candidate delivery is separate from default replacement. `tools/tui-candidate.py`
builds/seals source-pinned artifacts and installs/restores only an explicit private
candidate root. Inspect/install/restore require a manifest SHA-256 from an independently
trusted receipt; payload-supplied hashes alone are not trust. Windows also requires the
approved publisher/public-trust profile, timestamp and signed complete source commit.

The package seals the canonical `bin/.twig-version` marker for its native version.
Candidates deliberately omit classic companion binaries, so ordinary CLI startup must
not attempt a companion download or add files to the sealed installation. The hosted
gate executes a normal command in an uninitialized workspace and rechecks the complete
seal afterward; Windows artifact transport also preserves this required hidden file.
Earlier unmarked packages are not supported recovery payloads, even when their store
versions match. Do not run `twig upgrade` inside a candidate installation: use an
independently verified source-pinned package instead.

If the selected payload loses integrity, inspect and restore refuse without changing
the selection. Retain that root as evidence and install a verified marked package into
a fresh private candidate root. Do not delete unexpected files, relax the inventory or
re-seal the rejected payload to make it pass. This does not move or discard workspace
data, credentials, pending edits or journals.

Hosted Unix artifacts contain `<rid>.tar.gz` plus a separate seal receipt. Extract
the tar archive before inspect/install (for example, `tar -xzf linux-x64.tar.gz`);
do not upload or copy the raw Unix payload through a mode-stripping ZIP service.
The archive preserves executable permissions for the native image and launchers.
Rollback accepts only retained identical store/control contracts; no credential,
pending/journal mutation or incompatible downgrade is performed. Final default/plugin
switches and retirement require the epic's separate qualified owner cutover gate.

### Configure the current Bench

`bench configuration` reads explicit pins (including IDs not yet cached), saved
area and sprint filters, the existing automatic ownership scope, and a settings
digest. It does not fetch work items or edit workspace-global area/sprint settings.

```powershell
twig bench configuration -o json
twig bench configuration area add "Project\Team A"       # Under: this area and descendants
twig bench configuration area add "Project\Team A" --exact
twig bench configuration area remove "Project\Team A"
twig bench configuration sprint add @Current+1
twig bench configuration sprint add "Project\Release Sprint"
twig bench configuration sprint remove @Current
```

Read official paths without importing workspace defaults:

```powershell
twig bench configuration area candidates -o json
twig bench configuration area candidates --expect-bench 7 --expect-binding <binding> --expect-identity <identity> -o json
```

The read uses the configured team's actual `values` and `includeChildren` flags;
`defaultValue` alone does not imply an Under filter. Empty and failed reads stay
distinct. JSON carries `version: 1`, captured Bench/binding/identity IDs, `team`,
`areas` (`path`, `includeChildren`), and `settingsDigest`. Reading never changes
the configured team, workspace area defaults or saved Bench selectors. Select a
path and review an ordinary captured `bench configuration area add` operation.
Changing the Bench, origin or settings during the read refuses the observation.


Area alternatives are ORed; sprint alternatives are ORed; the area, sprint and
saved assignee constraints intersect. No areas means no area restriction. No
sprints means no automatic membership, never a project-wide query. Explicit
single/tree pins, seeds and pending work remain additive regardless of filters.
`@Current`, `@Current-N`, and `@Current+N` resolve using the cached iteration calendar
and roll forward with time. Absolute iteration paths stay literal. Refresh the
calendar and scoped candidates with `twig workspace sync`, not workspace-global
area/sprint configuration commands. Existing `current-sprint` rules are preserved
until an edit and then saved as a versioned `bench-filter` selector. The default
Bench refreshes its canonical self principal without discarding authored filters.

All four add/remove commands accept `-o human|json|minimal` and optional captured
preconditions: `--expect-bench`, `--expect-binding`, `--expect-identity`, and
`--expect-settings`. The settings digest covers raw stored query selectors, not
in-memory self normalization. Native storage compares the current Bench and digest
inside the same transaction that replaces only its query selectors; pins are
untouched. A stale form fails instead of retargeting or merging its old settings.
Unknown or malformed query rules refuse instead of broadening the scope.

JSON reads and successful edits expose `version`, `settingsDigest`, `areas`
(`path`, `includeChildren`), `sprints` (`expression`), `automaticEnabled`,
`assigneeSummary`, and `pins` (`id`, `mode: single|tree`, `cached`, and optional
cached `title`, `type`, `state`), plus captured Bench/binding/identity IDs.
The same configuration object appears at `browser.configuration` only when
`workspace --view tree -o json --include-browser` is explicitly requested;
ordinary workspace JSON remains unchanged.

Any positive work item ID can be pinned with `workspace track` or
`workspace track-tree`, even on an empty Bench. Unknown IDs are uncached/unverified,
not invented work-item records. Single and subtree pins for the same ID coexist.
`workspace untrack --mode single` removes only the single pin, and
`workspace untrack --mode tree` removes only the subtree pin. Omitting `--mode`
still removes both explicit kinds for that ID. None of these operations hides
inherited subtree membership, query matches, seeds or pending work.
These pin commands also accept `--expect-settings` for captured browser forms;
typed removal reports the named kind and whether the transaction actually
deleted it, then consumers can refresh native truth.

## Exit codes and failure modes

- `0`: the complete captured read or guarded local edit succeeded.
- `1`: cache miss, unavailable Bench, ADO/authentication failure, or changed origin,
  Bench or settings. No read retries or selects a replacement account/Bench.
- `2`: invalid arguments, including zero detail ID, unsupported detail `ids` output,
  invalid rendering width or malformed configuration selectors.
- Detail requires an existing cached Bench and never performs first-use initialization.
- Team candidates require a successful official team read; an empty collection is
  successful and distinct from a failed lookup. No guessed workspace paths are used.
- Error output is format-aware; JSON errors are emitted as an object on stderr.


## See also

- [`workspace`](../workspace/README.md) — the view that a Bench shapes.
