---
command: tui
group: experimental
summary: Open the in-process native TUI on Windows/Linux; macOS support is suspended.
stability: experimental
mutates: local
---

# `twig tui`

On Windows and Linux, `twig tui` opens the native Bench browser in the current
process. It is the same application as `twig bench browser`, with the same
captured source, authority checks, local editing and read-only proposal Review.
No companion download or Go/work-item-read subprocess is part of native startup.

macOS support is temporarily suspended. All macOS CLI/TUI entry points refuse
before workspace access; no legacy companion is downloaded or launched.

## Synopsis

```text
twig tui [--cwd <directory>] [--view tree|table] [--file <proposal>]
twig tui --herdr [--intent open|status|view|tree|table|review|exit-review|reconnect|close]
         [--view tree|table] [--file <proposal>] [--cwd <directory>]
         [--source-workspace <id>] [--source-tab <id>] [--source-pane <id>]
         [--source-socket <path>] [-o human|json|minimal]
twig tui --herdr --host-entry
twig tui --help
```

Windows/Linux packages contain identical native executable bytes under `twig`,
`twig-tui` and `twig-bench-tui` (`.exe` on Windows). The standalone names select
`tui` before argument parsing; pass flags directly, not another `tui` subcommand:

```text
twig-tui --cwd <directory> --view table
twig-bench-tui --file <proposal>
```

## Arguments

No positional arguments. All native options below are Windows/Linux options for
`twig tui`; `twig bench browser` retains its existing native entry surface.

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
|`--cwd`|string|Invocation CWD|Capture a source directory; never select an authentication identity.|
|`--view`|string|`tree`|Initial `tree` or `table` view; also used by Herdr `view` intent.|
|`--file`|string|None|Explicit read-only proposal; standalone opens Review, Herdr requires `review` intent. Relative paths resolve from the captured source directory.|
|`--herdr`|bool|`false`|Route source-scoped native Herdr controls instead of opening a standalone terminal.|
|`--host-entry`|bool|`false`|Manifest-owned entry; requires `--herdr` and a valid private launch admission.|
|`--intent`|string|`open` for Herdr|`open`, `status`, `view`, `tree`, `table`, `review`, `exit-review`, `reconnect` or `close`.|
|`--source-workspace`|string|Captured environment|Source workspace validated against the actual Herdr socket topology.|
|`--source-tab`|string|Captured environment|Source tab validated against the actual Herdr socket topology.|
|`--source-pane`|string|Captured environment|Source pane validated against the actual Herdr socket topology.|
|`--source-socket`|string|Captured environment|Herdr socket path; display IDs alone do not grant authority.|
|`-o`, `--output`|string|`human`|Herdr control result: `human`, `json` or `minimal`. Interactive terminal entry requires `human`.|
|`-h`, `--help`|switch|None|Print offline help and exit before workspace services or startup side effects.|

## Behavior

### Windows/Linux native entry

- The command resolves `BrowserHostCommand` and `BrowserRuntimeFactory` in-process
  through the same lightweight service registration as `twig bench browser`.
  Native startup skips companion first-run installation and binary cleanup.
- Terminal admission preserves inherited encoding, console modes and POSIX termios,
  restoring them on exit. The interactive entry requires a real terminal; redirected
  input/output is not a headless UI mode.
- Tree and Table browse admitted native data; selection does not change CLI context.
  Runtime badges/type colors, hierarchy connectors, owned chrome and selected-row
  backgrounds restore the browser presentation. Header/footer actions share guarded
  key/mouse dispatch; **?** exposes full captured context and scrollable guidance.
  R reads cache only. S explicitly pulls the captured positive item and links,
  never flushes pending edits or fetches unrelated targets. Seed S refuses.
  Lowercase s separately pulls the captured Bench's selectors and permitted context
  through the native workspace-sync operation, preserving protected pending work.
  View switches keep selection visible; Help/configuration exit restores the Bench
  reading viewport, and expanded arrows/j/k scroll physical content rows.
  v opens a focused full-width item view; E enters its guarded editor, and Esc
  returns through the same item reading position to the original Bench/inline view.
  Full detail uses separated title, metadata, long-form and relationship sections.
  The top-right header captures display-only terminal-host context automatically;
  Herdr labels use bounded read-only host queries, not work-item subprocess reads.
- Editing stages local pending changes through the shared native sink. No browser
  action publishes pending work. Review is read-only: opening a proposal does not
  authorize, apply or replace its digest, file or journal origin.
- Authority changes stop operations and clear actor data. Explicit reconnect creates
  a fresh native runtime; source IDs and override flags are never identity selectors.
- Herdr controls require `--herdr`; only `review` accepts `--file`. `--host-entry`
  accepts no cwd/file/intent/source overrides and requires private native launch
  admission. It does not turn caller-supplied source IDs into authority.
- The canonical Herdr host is `twig-herdr`; existing action and pane IDs remain
  unchanged. Windows/Linux entries launch the native image directly, not a wrapper.
  See [the native Bench browser](../bench/README.md#native-bench-browser) for gestures
  and authenticated host lifecycle details.
- Authenticated Herdr reconnect has a separate bounded cold-runtime dispatch
  budget of 15 seconds. Request authentication and reply/acknowledgement transport
  each remain bounded at five seconds; other control dispatches retain their
  five-second budget. Caller cancellation and owner shutdown still cancel work.
  No timeout triggers an automatic retry, retarget or replacement host.

### Suspended macOS entry

Both Intel and Apple Silicon macOS return exit 1 with an unsupported-platform
diagnostic. Dormant Terminal.Gui and native terminal sources remain for later
restoration, not as supported fallback paths.

## Examples

Open the default UI from a workspace on any supported platform:

```sh
twig tui
```

Open a native Table view from a captured Windows/Linux source directory:

```sh
twig tui --cwd /work/twig --view table
```

Review an explicit proposal without authorizing or applying it:

```sh
twig-tui --file .twig/proposals/review.json
```

Request the source-scoped Herdr Tree view and read its control result:

```sh
twig tui --herdr --intent tree -o json
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Native UI quits normally or Herdr control succeeds|`0`|
|Invalid view/output or incompatible control/host-entry options|`2`|
|Any macOS invocation|`1` with an explicit support-suspension diagnostic|
|Terminal, source admission or runtime fails; Herdr control refuses|`1`|

## See also

- [Native Bench browser](../bench/README.md#native-bench-browser).
- [`twig pending`](../plans/pending.md) — inspect local pending changes.
- [`twig sync`](../getting-started/sync.md) — explicitly synchronize pending work.
- [`twig upgrade`](../system/upgrade.md) — synchronize Windows/Linux native launch names.
