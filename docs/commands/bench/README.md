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
|`bench discussion --stdin`|Read one remote discussion page for an explicitly qualified cached Bench member.|none|
|`bench configuration area candidates`|Read official configured-team paths and Exact/Under semantics.|none|
|`bench browser`|Shared native Bench terminal, local editing and read-only Review; Windows/Linux `twig tui` uses the same application.|local only; explicit S pulls selected root/links|

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

Full detail separates the complete title, metadata, long-form fields and relationships
with headings and spacing. Metadata labels sit above their wrapped values; assignee,
paths and related titles remain complete even in narrow panes.
Markdown/HTML descriptions retain blank-line separation before, after and between
headings, as well as between paragraphs; flattening rich content no longer removes
those spacers.

**v** opens the selected work item in a focused, full-width read-only view, without
surrounding Bench rows. This is item inspection, not proposal Review. Scrolling and
resize retain the same field/HTML reading anchor. **E** enters the existing guarded
editor for that item; **Esc** returns to its reading position, then another **Esc**
restores the original Bench or inline-detail viewport. Unsaved editor buffers remain
retained until explicitly discarded or staged. R/S retain their cache/pull-only meaning.
Multi-select and shared-field batch editing are not part of this view.

Resizing retained detail keeps its reading position within the native field or HTML
section, including literal diagrams, rather than retaining a rendered row number.
Near the end, widening may leave blank rows below the retained content; **End** still
selects the normal last full page. While detail is expanded, arrows/j/k, wheel and
Page keys scroll physical content rows, including the middle of long items. When
collapsed, arrows/j/k select Bench items. Private rendering source markers are
removed before painting and are not part of public CLI ANSI output.

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

### Semantic detail export

```powershell
twig bench detail 42 --document -o json --expect-bench 7 --expect-binding <binding-id> --expect-identity <identity-id>
twig bench detail 42 --document --sync -o json
```

`--document` opts into **version 2**, a read-only semantic document for native
canvas consumers. It requires `json`, `json-full` or `json-compact` output and
replaces the ANSI envelope, not the existing default version-1 contract. No ANSI
parsing is needed. The flag explicitly permits a native form-metadata read for a
published item's actual type; it does **not** refresh that item. `--sync` remains
the only selected-item pull. Local seeds use the labelled fallback without a
server layout read; combining seed detail with `--sync` refuses as before.

The export carries:

- `itemKey`: checkout root, admitted connection ID, binding ID and work-item ID.
  Revision is not part of the key. A later revision keeps the same item identity
  but changes the observation baseline; it grants no automatic draft rebase.
- `item`: the full cached snapshot, including revision, last sync time, fields
  and local dirty state. `hasLocalChanges` also accounts for pending notes and
  pending-change records. These may be local values, not current server values.
- Captured Bench/identity IDs, binding and selection revisions, and storage
  generation when available. These qualify the observation, not a Save or Apply.
- `layoutStatus`: `served`, `locked`, or `unavailable`. `layoutSource` is `server`
  only for served layouts; otherwise it is `twig-fallback`. Locked results also
  retain `lockedTypeReferenceName`. An empty served layout remains empty, never
  synthesized. Provider failures refuse output rather than becoming a fallback.
- `document`: the native page/section/group/control order, visibility,
  contributions, system controls, server read-only flags and full values.
  Value `state` retains native codes: `0` HasValue, `1` EmptyOnServer, and `2`
  NotCarriedByTwig. Missing transport is not a blank server field. Non-custom
  pages are structure only, not fetched history, links or attachments.
- `appearance`, when cached native type metadata exists: actual type name, color
  and icon ID, never inferred from a type-name table. `appearanceSource` identifies
  cached metadata or its absence. `iconAssetAuthority: "unavailable"` means that
  metadata grants no permission to fetch an arbitrary URL or claim a supported asset.

`editing: "unavailable"` is intentional. A server control's `readOnly: false`
does not authorize editing. This increment does not export persistable-field
capabilities, mutation captures, discussion/files or a draft owner.
`relatedItemsCoverage: "not-exported"` means no conclusion about absent
dependencies is possible; `linksVerifiedAt`, when present, does not change that.
Consumers must treat raw HTML/text as untrusted content and render it inertly.

The complete metadata acquisition and projection run inside the existing native
operation guard. Origin or Bench drift and cancellation emit no observation.
No active item, binding, default, proposal or journal is changed by this export.
This is a native read foundation for Epic #1172, not an installed Copilot extension.

### Read one discussion page

```powershell
twig bench discussion --stdin -o json < request.json
```

`--stdin` is required. The command reads one strict version-1 JSON request through
EOF: at most 262144 UTF-8 bytes, depth 32, no unknown or duplicate members. The
request names the qualified `itemKey`, `bench`, `expectBench`, `expectIdentity`
and a `cursor` (`null` for the first page). The item must be a cached member of
that Bench.

Each call reads at most one provider page (50 comments, 2 MiB). The result carries
the comments, a `nextCursor` and `snapshotConsistency: "not-guaranteed"`. Pages are
not a consistent whole-thread snapshot. The cursor is untrusted paging data, not
authority, and a context mismatch refuses. Human and minimal output print one line
per comment plus a `discussion` paging line; `ids` output is refused.

This read never changes the current item or Bench, never saves or refreshes the
cache, never posts comments, and never follows supplied URLs. Non-ADO providers
refuse with `provider-unsupported`.

### Native Bench Browser

`twig bench browser --cwd <workspace> --view tree` opens the shared native terminal
application; `--view table` selects Table. It uses admitted native modules in-process,
not a Go UI or read subprocess. On Windows/Linux, canonical `twig tui`, `twig-tui`
and `twig-bench-tui` enter this same application with the same options. macOS
`twig tui` and its Terminal.Gui companion remain unchanged.

- **Browse:** 1 Tree, 2 Table; arrows/j/k select locally. Left folds or selects the
  parent; Right unfolds or selects the first child; Space toggles a fold. Tree
  connectors, disclosures and a `›` selection marker remain visible without color.
  Enter opens or closes full inline Show beneath all wrapped lines of the item.
  Mouse disclosure/click/double-click and wheel use the same continuous surface.
  Page/Home/End scroll; resize reflows retained data. View switches keep a renderable
  selection visible. Selection never calls `twig set`. The native 1 Tree / 2 Table
  mapping is unchanged; it differs from the original Go browser's digit mapping.
- **Read:** R is cache-only. S explicitly pulls the captured positive item and links,
  never pushes pending work or fetches unrelated targets. Seed S refuses. Retryable
  native checkout-lease or registry contention retains cached content without admitting
  a read or write; retry after release rechecks the captured origin. Confirmed authority
  changes clear visible/hidden state and stop operations until explicit Ctrl+R reconnect
  creates a fresh native runtime. Other retryable same-origin failures retain content too.
  Lowercase **s** is separate: it explicitly pulls the captured Bench's selectors
  and permitted relationship context through the same native operation as
  `twig workspace sync`. It protects pending work, skips seed publication and
  rechecks the captured Bench/origin before publishing the result. No navigation,
  configuration entry or repaint implicitly runs either sync action.
- **Configure:** b or c opens Pins/Areas/Sprints/Benches. Tab/Shift+Tab or digits
  choose a section; arrows select sections until Enter/Right focuses entries;
  Left returns to sections. Pins use independent p/P Single/Subtree toggles;
  i/I or a/A opens a visible manual-ID field, including uncached IDs shown as
  unverified. Areas use a/d, x Exact/u Under, and an explicit t
  configured-team candidate read. Sprints use a/d and native expressions. Benches use
  n create, Enter switch, and d guarded delete; Default is protected and stale contents
  require a new confirmation. Nonempty deletion lists preserve selector kinds, so
  Single (`Item`) and Subtree pins for the same ID remain distinct review consequences.
  These operations preserve pending work. Creating a Bench highlights it without
  switching. Esc cancels a form first, then restores the previous Bench selection,
  folds and reading viewport when leaving configuration.
- **Edit:** E keeps the rendered item presentation: title, metadata, rich descriptions
  and relationships, with the selected field highlighted. Enter changes that field
  into an inline text input; Tab retains it and moves to the next served field.
  Additional unsupported/not-carried document controls remain identified below the item.
  Typing is limited by the shared local sink (Title/State/Assigned To), not server
  read-only metadata. Ctrl+S stages one local batch.
  Unsupported/missing values or invalid transitions refuse; failed saves retain input.
  Esc returns without discarding; Ctrl+D explicitly discards only unsaved input.
  Saved pending values survive viewer restart. No UI action publishes them.
  A seed with no initial state can stage a state from its discovered type's catalog
  in the same batch as Title/Assigned To. Unknown configured state names refuse the
  entire batch and retain input; this does not grant published items an empty-state
  transition or authorize seed publication.
- **Review:** 3 opens the latest qualified unresolved native proposal, or start with
  `--file <proposal>` to capture one explicit file. D details, B brief and Esc/c back
  remain read-only. Original digest/file/journal origin are rechecked; a changed
  proposal clears old content rather than silently substituting a replacement.

The header identifies the captured connection, team, Bench and user; the context
row identifies the view, worktree and admitted branch. Clickable view buttons use
the same key path. Narrow panes abbreviate labels with an ellipsis and use compact
view buttons; **?** opens scrollable help with complete captured context and all
gesture/configuration guidance. Esc returns to the same Bench viewport.

The top-right header automatically shows captured terminal-host context. Herdr
workspace/tab labels and the current pane come from bounded, read-only installed
Herdr CLI queries; compact panes retain the host/pane label. Help exposes the full
capture, including the source pane for admitted hosting. tmux/Zellij environment
hints are display-only and explicitly unverified. None of this metadata selects an
identity, changes routing authority, or changes the registered extension. Unavailable
host metadata does not prevent the browser from opening.

Runtime process appearance and user overrides supply work-item badges/type colors;
state categories supply redundant state badges. Selection and chrome own their
backgrounds, and configuration has a distinct surface. `NO_COLOR` or `TERM=dumb`
removes styling without removing the selection marker or gesture labels.
Full item and inline detail use the same dark content surface across plain text,
headings and emphasis, without reverting individual spans to the terminal's default
background. Secondary text does not depend on faint rendering. Edit-field focus uses
the restrained blue selection surface rather than inverted blocks; the `›` marker
and input caret remain visible without color.

The footer stays on the last visible row, with a separate notice row above it.
Named, fully visible action buttons are mouse-clickable; clipped controls cannot
execute hidden actions. Pin/Unpin reflects cached explicit Single/Subtree pins,
not inherited membership. q quits from the Bench when no unsaved buffer needs a
decision. Modes, inherited code pages and POSIX termios are restored on exit.
Native terminal code targets win-x64/linux-x64/osx-arm64; source or an unsigned
smoke build is not signed installed-platform qualification or owner baseline acceptance.

Wrapping keeps extended graphemes intact. Windows budgets their per-rune console-cell
advances and reflows overflowing native Show/Review ANSI rows before viewport clipping,
retaining text, active styling and the left panel edge. Unix keeps its existing width
and native-row policy. A cluster too wide for the current viewport remains in the
retained text for later wider reflow; font-specific rendering still needs qualification.

`--herdr --intent open|status|tree|table|view|review|exit-review|reconnect|close`
routes authenticated bounded controls to the same application in a native
manifest-owned pane. Windows/Linux uses the canonical `twig-herdr` host, preserving
existing action and pane IDs; the manifest source is
`integrations/herdr-aot/herdr-plugin.toml`. macOS's existing launch paths remain.
Installed protocol22 was the measured capability
contract: plugin-pane open/close, opaque owner metadata, pane get/list/process-info,
captured source topology and no-focus launch. Display IDs alone are not authority.
Both manifest pane entries launch the native image directly, including Windows;
the exact reported native root PID (or a positive foreground PID) is required.
Control protocol 4 uses exact process birth identifiers as well as short unpredictable
pipe names and acknowledged nonce/owner/source-qualified replies. Linux identifies a
process by its kernel boot UUID and `/proc` start ticks; a per-process reconstructed
wall-clock timestamp is not portable evidence of the same live owner. Windows and
macOS use the kernel-reported UTC creation stamp. PID-only checks and time tolerances
are not substitutes. Version-1/2/3 registrations refuse rather than being adopted;
recovery requires the same control/store contract. Authentication tokens and nonces
remain 256-bit.
Malformed, truncated or disconnected clients do not retire the owner registration.
Accepted pipes disconnect even after EOF marks them broken; OS-default buffers avoid
retuning an already-disconnected Unix peer during accept. The next authenticated request
uses the same owner, without replacing the terminal or relaxing control checks.
An uncatchable kill may leave a stale private registration; explicit proved-dead
owner recovery never steals a slow live peer. Supported legacy migration requires
explicitly closing the native host; no native quiescence exemption is invented.

Source-pinned delivery remains independently sealed. `tools/tui-candidate.py`
builds/seals artifacts and installs/restores only an explicit private
candidate root. Inspect/install/restore require a manifest SHA-256 from an independently
trusted receipt; payload-supplied hashes alone are not trust. Windows also requires the
approved publisher/public-trust profile, timestamp and signed complete source commit.

The package seals the canonical `bin/.twig-version` marker for its native version.
Windows/Linux includes same-image `twig-tui` and `twig-bench-tui` launch files before
sealing, not classic companions. Native startup does not discover or download
separately built TUI companions. The hosted gate executes a normal command in an
uninitialized workspace and rechecks the complete seal afterward; Windows artifact
transport also preserves the required hidden marker.
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
pending/journal mutation or incompatible downgrade is performed. Installed default/plugin
selection and legacy retirement require qualified owner-authorized cutover; changing
source defaults does not itself publish a public release or mutate registration.

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
  invalid rendering width or malformed configuration selectors. For `bench discussion`,
  also a missing `--stdin`, an invalid or oversized request, or `ids` output.
- `bench discussion` exits `1` on a typed refusal, for example `bench-item-not-member`,
  `bench-item-not-cached`, `authority-invalidated`, `discussion-denied`,
  `discussion-not-found`, `discussion-offline`, `discussion-throttled` or
  `provider-unsupported`. JSON output still carries the refusal object.
- Detail requires an existing cached Bench and never performs first-use initialization.
- Team candidates require a successful official team read; an empty collection is
  successful and distinct from a failed lookup. No guessed workspace paths are used.
- Error output is format-aware; JSON errors are emitted as an object on stderr.


## See also

- [`workspace`](../workspace/README.md) — the view that a Bench shapes.
