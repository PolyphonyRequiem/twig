# Feature 1143 — AOT terminal and native Herdr feasibility checkpoint

## Verdict and execution boundary

**Bounded Windows NativeAOT, actual Windows Terminal and installed native-Herdr feasibility demonstrated; tracker closeout remains separate.** The coordinator exercised source-matched native prototypes standalone in Windows Terminal and in native manifest-declared Herdr panes, including actual Windows pointer routing, wrapped content, viewport anchors, cancellation and control lifecycle. The user's expansion-jump finding was corrected, then independent review found and prompted a second correction for Home/End during loading. Universal font/Unicode conformance, Linux/macOS execution, production authority/store/sync and final visual Show parity are not claimed. Terminal strategy and consequential product decisions remain owner gates.

Repository-owned evidence: `samples/Twig.TuiAotPrototype1143/evidence/host-inspection.json` records actual installed-host/SDK/schema discovery; `evidence/verification-summary.json` retains selected coordinator-executed records, visible fixture content, restoration observations, unsuccessful regressions and corrected results without instance tokens. Full raw evidence remains at `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/prototype-herdr/{host-interactions.json,standalone-restoration.json,private-channel-refusals.json}`. Source and evidence are explicitly retained opt-in throwaway artifacts, not defaults or production cutover.

Earlier frozen source/report evidence was independently reviewed. The final viewport/navigation correction receives a fresh scoped source review and criterion assessment; neither review is native publication authority. Feature1144 is not started or automatically unblocked.

**Source/build provenance:** the original accepted start gate and build/fingerprint evidence remain historical and retained. Updated full-trim NativeAOT candidates are isolated in `publish/viewport-fix` and `publish/viewport-fix-v2`. The final v2 source-matched publish plus eight actual-Browser smoke scenarios succeeded in a combined 29.90 seconds; actual Windows Terminal screenshots additionally prove End during loading retains the selected following viewport and Home during reload returns to scroll0. No stable runtime/install output is replaced. Historical restoration/control proofs describe the earlier unchanged terminal/control seams, not a fabricated re-execution on every candidate.

**Unsuccessful attempts are retained, not erased:** same-source Table initially refused cwd trailing-separator spelling drift; centralized lexical source-path normalization fixed it, and corrected native open/Table retained PID32056/sourcew2:p1. A later replay with Token:null initially crashed with NRE/exit9, then validation correctly rejected it but the CLI still lacked typed InvalidDataException handling (exit9). Final same-case replay exits1 with `AOT1143 prototype: Incomplete prototype registration; preserved, not reclaimed.` Main verified the malformed file remained unchanged before explicitly removing only that owned dead fixture. Opaque host IDs/endpoint remain exact; no global NRE suppression or automatic malformed-data reclaim was introduced.

## Acceptance-criterion ledger

| Criterion | Observed evidence now | Missing evidence / gate | Current result |
|---|---|---|---|
| **AC-1: current NativeAOT/trim build and Windows Terminal + installed Herdr execution** | Full-trim .NET10 NativeAOT candidates executed in actual Windows Terminal and installed native Herdr manifest panes, including final v2 navigation. | Windows-only measured support; Linux/macOS anticipated, not claimed supported. | **Satisfied for the specified measured prototype; independently reviewed.** |
| **AC-2: keyboard/mouse, wrapping, open-right detail, HTML/title tails, scroll/anchors** | Actual Windows GUI disclosure/row clicks and wheel0→3; corrected initial load, reading reload, following-row anchors, resize and collapse; final v2 Home/End-during-load proof in GUI and native Herdr; pinned footer. Prior complete tails retained. | Flag glyph fallback, explicit native ZWJ escaping and bounded cell-width approximation are disclosed, not universal font or production visual parity. | **Satisfied for measured prototype behavior; independently reviewed.** |
| **AC-3: same executable standalone + manifest pane + external view intent with source context** | Native tree-windows action changed existing PID27684 Table→Tree with sourcew2:p1; same payload standalone; repeated open preserves PID/view/detail/scroll; actual different panew2:p5 refused; final latest payload open→Table samePID42032. | Owner host-seam acceptance; Herdr action child did not inherit custom state-dir variable, so demonstrated action uses consistent default prototype-only namespace. | **Native same-executable/control slice proved with named namespace limitation.** |
| **AC-4: restoration, cancellation/fences, closed/stale registration** | Standalone exact input503/output7/CP437 restoration and exit0; generation4/item2 loaded while3 old results dropped; cooperative cancellation1; wrong-token refusal/malformed-source disconnect with liveUI healthy; normal close/no-live status/valid closed replay refused; final Token:null exit1/file preserved. | Slow-live-peer timeout, replacement-token cleanup, forced-kill cleanup and other required-field-null rows are not individually executed; source guards are not substitute proof. | **Specified executed cases proved; remaining adversarial/platform coverage explicit.** |
| **AC-5: independently reviewed report, seams/build/blockers, disposition/no stable changes** | Final criterion and corrected-source reviews retained; actual native-backed closeout accepted, applied and refreshed-verified with all Feature gate fields and completed outcome. | Product terminal/platform/retirement/cutover decisions remain separate owner gates. | **Bounded prototype evidence accepted; Feature1143 Done revision13.** |

## Prior work: neither dismiss Terminal.Gui nor overclaim its proof

Read the complete captured live snapshots at:

- `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/item-139.json` — Task 139 is captured as To Do. Its description records the spike's Linux results, its Windows verification intent and the distinction between publishing and executing.
- `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/item-1113.json` — Task 1113 is Doing and owns shared-binding TUI reconnect under Spec 1103. The prototype must not duplicate, reparent, transition or take over this work.

`Directory.Packages.props:25-28` pins **Terminal.Gui 2.0.0-develop.5185**. That is the same package recorded in `0a8c185d:AOT-SPIKE-FINDINGS.md`. The current `src/Twig.Tui/Twig.Tui.csproj` disables AOT and trimming; its comment is not proof of a library impossibility.

Historical spike findings, read with `git show 0a8c185d:AOT-SPIKE-FINDINGS.md`:

1. Baseline diagnostics were IL2026/IL3050 at `Init`.
2. Suppressing diagnostics produced a roughly 12 MB native executable which **crashed before Main**: `Theme is not a ConfigProperty`, from Terminal.Gui's configuration module initializer.
3. Rooting Terminal.Gui and the TUI assembly, with `TrimMode=partial`, preserved the required reflection metadata. The roughly 19 MB Linux executable actually rendered, accepted keys and exited 0 in a tmux pseudo-terminal.
4. This is a trim/configuration caveat, not a general inability to generate native code. The historical workaround suppresses analysis and loses full-trim assurance. It must not be silently adopted as the new full-trim contract.

**There is also historical Windows proof.** `wayfinder-1.0/tickets/1002-fold-the-tui-into-one-binary.md:15-20` records owner-run Windows 11 evidence dated 2026-08-06: 19.2 MB native output, complete old-TUI draw, input and exit 0, with four nonfatal IL3051 diagnostics. It links [the owner result](https://github.com/PolyphonyRequiem/twig/issues/359#issuecomment-5209252542). The Linux spike's own untested-Windows statement predates this follow-on; it must not be restated as the overall evidence status.

Neither historical record qualifies current Bench wrapping, mouse, resize, HTML tails, continuous detail scrolling, the installed Herdr, or the proposed .NET 10 prototype. Task 139 remains a separately owned tracker item; this report does not close it.

## Existing native reuse boundaries

### Domain projection probe, not a terminal runtime

`samples/Twig.DetailHost/Twig.DetailHost.csproj` intentionally references only Twig.Domain and targets .NET 10. `Program.cs:27-83` projects `WorkItemDetailDocument`, asks for appearance separately, hands it to a caller-owned pane and exercises host-owned selection/scroll/expanded values. `HostPane.cs:23-235` is plain-string rendering, not real input or terminal handling. Its fixed geometry, clipping and independent scroll model must not be presented as Windows Terminal or Herdr acceptance.

The reusable lesson is the boundary: domain facts and edit capabilities are separate from host presentation. Do not port its string clipping into the Bench grapheme/wrapping experiment, and do not imply the current read-only prototype retires old-TUI field editing/staging. That disposition belongs to the owner and Feature 1142's preservation matrix.

### Native Show operation already owns authority

`src/Twig/Commands/BenchDetailCommand.cs:39-108` already owns:

- operation admission and a repository lease;
- captured binding/identity and stored Bench checks;
- cached item, field definitions, status entries, children, parent, links and pending changes;
- selected-root/link-only explicit pull; negative seeds are not sync targets;
- `SpectreRenderer.BuildStatusViewAsync(... fullContent: true)`;
- final captured-origin and operation rechecks after yielding reads/rendering.

The terminal adapter must not call a second auth chain, read raw SQLite, or regard item IDs, cwd or control tokens as ADO authorization. Shared in-process extraction belongs to Feature 1144 after accepted evidence, not this fixture prototype. Pure scrolling and reflow operate on already obtained in-memory content, with no shell/read/fetch on the navigation path. No idle permanent operation lease is introduced.

### Full-title, HTML and open-right presentation

`src/Twig/Rendering/SpectreRenderer.cs:1255-1296` includes complete HTML content for `fullContent`, adds the complete title into the panel body, and uses `OpenRightBoxBorder`. Lines 1302-1313 map top-right/bottom-right to straight square rules and right edge to whitespace: no right vertical or right corner.

`src/Twig/Rendering/RichHtmlRenderer.cs` is an inert shared parser/renderer. It keeps HTML text tails, skips active content, makes safe URLs visible without OSC hyperlink control, and stacks narrow table cells instead of eliding them. It emits `RenderNode` values; `ToMarkup` alone is not a complete adapter for its table nodes. A prototype must walk or render **all emitted variants**, including tables, rather than use a text-only shortcut which loses content.

A bounded isolated prototype can link this existing source and the minimal dependency-free RenderTree types read-only, and use the pinned Spectre.Console 0.57.2. It need not build/reference production projects or acquire a store. The whole full-Show renderer has broader theme/domain/formatter dependencies; do not clone its complete implementation merely to claim reuse. The prototype can honestly demonstrate the existing native HTML parser plus host-owned title/frame layout, with **complete Show parity still an extraction/qualification gate**.

One consequential Unicode limit is already visible: `RichHtmlRenderer.SafeText` escapes Unicode format characters, including a ZWJ. A raw Bench title fixture may demonstrate intact extended grapheme wrapping while native Show presentation deliberately displays escaped format characters. Do not silently change production sanitization or advertise that these are identical Unicode policies. An owner-reviewed safe-text/grapheme policy is needed if full-title presentation should preserve joined emoji.

## Bounded experiment choice, not the production choice

For the next authorized prototype slice, the recommended bounded option is a **Windows-specific Win32 INPUT_RECORD loop plus VT output**, with existing native HTML rendering and a separate source-generated JSON control adapter. This decision is for the isolated experiment only and is not an owner choice of production terminal toolkit.

| Option | Relevant observed strengths | Remaining risk / disposition |
|---|---|---|
| Terminal.Gui develop.5185 | Historical actual Linux and Windows old-TUI NativeAOT execution; existing old-TUI code already uses it. | Config/module initializer trim trap; historical root+partial workaround does not meet full trimming unchanged; Bench detail/layout/mouse/current-host coverage absent. Retain as a serious alternative, not rejected as incapable. |
| Raw Win32 input + VT output | Official console API exposes key, mouse and window-size input records; source-generated P/Invoke can remain NativeAOT-friendly. Small Windows slice avoids library module initializers and creates an actual terminal executable. | Windows-only input seam; mouse forwarding/ConPTY behavior, cell widths, shutdown and host buffering must be measured. It is not a portable product design and should not grow into a home-built cross-platform terminal toolkit by accident. |
| Cross-platform VT input parser/library | Aligns with Microsoft's preferred modern terminal ecosystem. | Not selected or executed; requires bounded package/version/AOT and key/mouse sequence qualification rather than assuming compatibility. |
| Browser/HTML prototype or plain-string DetailHost | Useful for other questions. | Does not answer this Feature's actual terminal/native-host requirement; not selected as substitute proof. |

[Microsoft's console-mode reference](https://learn.microsoft.com/en-us/windows/console/setconsolemode) specifies `ENABLE_MOUSE_INPUT`/`ENABLE_WINDOW_INPUT`, disabling QuickEdit via extended flags, and the difference between input records and VT input. The [mouse structure reference](https://learn.microsoft.com/en-us/windows/console/mouse-event-record-str) explicitly recommends VT for new cross-platform products while preserving classic APIs; that warning is a reason to keep the Win32 experiment bounded. [ConPTY documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session) explains that host geometry determines client console buffer answers and that host resizing is distinct from application reflow.

## Installed native Herdr contract — actual read-only observation

Primary captured evidence: `samples/Twig.TuiAotPrototype1143/evidence/host-inspection.json`.

- Installed binary: `herdr 0.9.1-preview.2026-09-29-9dc3a1df2b56`.
- API schema: protocol 22, schema version 1; captured `PluginPaneOpenParams`, `PluginInvocationContext`, `PluginLinkParams`.
- CLI pane launch supports plugin/entrypoint, explicit target pane, cwd, environment, direction and `--no-focus`.
- Schema additionally lists popup placement, while current `plugin pane open --help` CLI lists overlay/split/tab/zoomed only. Use the **observed split CLI surface**, not schema-only popup syntax.
- Registered `twig-herdr` entry reports GitHub provenance `@v0.2.4`. The actual installed manifest at `C:/Users/dangreen/AppData/Roaming/herdr/plugins/github/twig-herdr-145da1c6cf2a/herdr-plugin.toml` says version **0.4.0**. These are different observed facts; do not collapse provenance into payload version.
- Actual stable actions include open, table, tree, exit-review and reconnect, with Windows-specific IDs. Native pane entrypoints are bench/bench-windows. `prefix+t` in the existing config targets `twig-herdr.open-windows`.
- The stable Windows manifest uses a tiny PowerShell argv launcher resolving `HERDR_PLUGIN_ROOT`; the pane executes the native payload. This is not a .NET in-process widget SDK.
- Nothing was linked, installed, unlinked, reconfigured, registered, focused, launched, closed or updated by this worker.

### Implemented and exercised isolated host/control seam

Owned source under `samples/Twig.TuiAotPrototype1143/**`:

- `Directory.Build.props`, `global.json`, `Twig.TuiAotPrototype1143.csproj`: GA .NET10.0.401 SDK, isolated bin/obj, full trim/AOT, reflection JSON disabled, source-generated P/Invoke/runtime-marshalling boundary. No production project references or build outputs; only pinned Spectre and explicitly linked existing native presentation sources.
- `WindowsTerminal.cs`: Windows INPUT_RECORD keyboard/mouse loop and native geometry sampling; VT alternate screen, cursor and input/output-mode cleanup. This Windows-only seam is not a cross-platform production strategy.
- `Presentation.cs`: in-memory fixture hierarchy and complete title/HTML sentinels; indivisible extended-grapheme wrapping with an explicitly bounded cell-width approximation. Calls the existing `RichHtmlRenderer.Render`, handling **all currently emitted Text/Markup/Table variants**, including every table cell. Uses a stable semantic parse width and host-stacked table cells so block IDs do not change on resize. Rich styles are intentionally reduced to plain text plus selection styling; complete Show/visual styling parity is not claimed.
- `Browser.cs`: event-loop-owned mutable state, one Bench/detail scroll surface, inline square open-right frame, folds/selection/semantic viewport capture+restore, visible following-row anchor preference during height changes, fixture-load cancellation/request+generation fences, intentionally noncooperative late-result mode, and visible counters. Parsed native detail blocks are retained per accepted load; pure scrolling/reflow does not shell, fetch or reparse HTML.
- `Control.cs`: source-generated framed JSON contracts, CurrentUserOnly named pipes, random instance pipe/token, exclusive registration, process start identity, captured source checks, acknowledgment after event-loop application, own-token cleanup and cancellation deadlines. These tokens are local UI control capabilities, **not ADO authority/credentials**.
- `HerdrHost.cs`, `Program.cs`: same executable has panel/open/tree/table/status/close modes; controller requires explicit `--pane`, actual Herdr environment and endpoint. Optional `TWIG_AOT1143_HERDR_SESSION` selector must match endpoint directory. New native panes receive immutable source workspace/tab/pane/cwd separately from their own host ID. Different source capture refuses reuse; status/close cannot create a pane; a timeout/inaccessible peer is not stale. A dead process whose host pane still exists is refused until the coordinator explicitly closes that captured prototype pane.
- `herdr-plugin.toml`, `launch.ps1`: separate plugin `twig-tui-aot-1143`, actions open-windows/tree-windows/table-windows/close-windows, pane bench-windows. Thin PowerShell argv launcher invokes the same NativeAOT executable, with no Go or private read companion. Missing payload/capture refuses; no JIT/install fallback, stable ID, hotkey, build/install hook or product registration is introduced.

Default ephemeral control root is the dedicated temp namespace `twig-tui-aot-1143-control`; Main can set `TWIG_AOT1143_STATE_DIR` to an experiment-owned folder. Same-user **and elevation-level** constraints apply to CurrentUserOnly pipes. Registry scope is session endpoint/workspace/tab; original source-pane/cwd remains immutable and is checked in every request. A different source pane in the same tab therefore refuses rather than retargets.

`SourceContext` canonicalizes cwd once for captures and source-generated JSON deserialization, strips only non-root trailing separators and compares Windows directory paths case-insensitively. Opaque endpoint/workspace/tab/pane identity remains ordinal and immutable; lexical path normalization does not resolve symlinks or prove filesystem/ADO authority.

Fixture fences demonstrate UI late-result isolation only. They do **not** prove real binding/principal/cache admission races, real selected-root/link sync, or durable pending work. Task1113 and Feature1144 retain authority extraction/reconnect work; S explicitly reports unavailable rather than simulating a production sync success.

Stable lifecycle references remain `C:/Users/dangreen/projects/twig-herdr/control.go:145-208,217-325,432-455`. No stable control source was modified.

## Reproducible discovery commands (already exercised)

From the working root; all are read-only:

```powershell
herdr --version
herdr api schema --json
herdr plugin list
herdr plugin action list --plugin twig-herdr
herdr plugin pane open --help
herdr plugin action invoke --help
herdr plugin link --help
dotnet --list-sdks
git show 0a8c185d:AOT-SPIKE-FINDINGS.md
```

SDK inventory includes 10.0.112, 10.0.201 and 10.0.401, plus the current 11.0 RC and 8.0 SDK. Windows NativeAOT still requires the C++ toolchain; [Microsoft's NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) require Visual Studio's Desktop development with C++ workload. Toolchain readiness was **not validated** in this worker slice.

## Exact isolated publish and execution commands (coordinator-owned)

The project/source/manifest and coordinator-published native payload exist. Main's final8.95s source-matching publish and subsequent native valid control/close/null-registration replay passed. Reproduce from the owned directory so its pinned .NET10 SDK/local props apply; outputs remain inside it. Preserve actual diagnostics; no AOT/trim suppression or JIT fallback.

```powershell
Set-Location C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic/samples/Twig.TuiAotPrototype1143
dotnet publish Twig.TuiAotPrototype1143.csproj -c Release -r win-x64 -o publish/win-x64
# Actual console/ConPTY only; redirected input/output is deliberately refused.
./publish/win-x64/twig-tui-aot-1143.exe panel
```

Coordinator proof used named session `twig-aot1143-20261010` with separate kickoff/prototype-herdr/config.toml. Initial source was w1; accepted actual interaction records capture source workspacew2/tabw2:t1/panew2:p1 and owned UIw2:p4. An isolated Herdr rendering client was later attached through a supervised PTY so real PTY geometry followed dividers. These IDs are actual returned captures; re-read before mutations. This is installed Herdr/ConPTY proof, not physical Windows Terminal GUI/pointer acceptance, and never changed the original stable global config/plugin/hotkey.

Only Main may link the separate prototype manifest into that approved isolated session. Use its actual environment rather than inventing `HERDR_ENV=1` outside a managed context. The controller validates the endpoint against the explicit session selector:

```powershell
# In the coordinator's actual isolated-Herdr environment after publish:
$env:TWIG_AOT1143_HERDR_SESSION = 'twig-aot1143-20261010'
$env:HERDR_SOCKET_PATH = 'C:/Users/dangreen/AppData/Roaming/herdr/sessions/twig-aot1143-20261010/herdr.sock'
# Native action proof used the DEFAULT prototype-only control namespace consistently:
Remove-Item Env:TWIG_AOT1143_STATE_DIR -ErrorAction SilentlyContinue
herdr --session twig-aot1143-20261010 plugin link C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic/samples/Twig.TuiAotPrototype1143 --enabled
# Preferred launch: controller captures the explicit source and passes all immutable env values.
./publish/win-x64/twig-tui-aot-1143.exe open --pane w2:p1
./publish/win-x64/twig-tui-aot-1143.exe table --pane w2:p1
./publish/win-x64/twig-tui-aot-1143.exe status --pane w2:p1
./publish/win-x64/twig-tui-aot-1143.exe tree --pane w2:p1
```

Native direct pane command for the same captured source (do not run in addition to a live controller-opened instance):

```powershell
herdr --session twig-aot1143-20261010 plugin pane open --plugin twig-tui-aot-1143 --entrypoint bench-windows --target-pane w2:p1 --direction down --cwd C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic --env TWIG_AOT1143_SOURCE_PANE=w2:p1 --env TWIG_AOT1143_SOURCE_WORKSPACE=w2 --env TWIG_AOT1143_SOURCE_TAB=w2:t1 --env TWIG_AOT1143_SOURCE_CWD=C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic --env HERDR_SOCKET_PATH=C:/Users/dangreen/AppData/Roaming/herdr/sessions/twig-aot1143-20261010/herdr.sock --no-focus
```

Native action verification (actual action invocation must supply the original source `HERDR_PANE_ID`; no focused-pane query fallback exists):

```powershell
herdr --session twig-aot1143-20261010 plugin action invoke table-windows --plugin twig-tui-aot-1143
herdr --session twig-aot1143-20261010 plugin action invoke tree-windows --plugin twig-tui-aot-1143
./publish/win-x64/twig-tui-aot-1143.exe close --pane w2:p1
```

Inspect the returned ControlReply snapshot and actual pane output. Same PID/source/host registration and visible mode establish delivered reuse, not an action-process exit alone. Close cancels the UI first, then targets only the instance's captured opaque plugin-pane ID. If the host already auto-removed the pane, report the observed close result rather than claiming the cleanup command necessarily succeeds.

**Observed host environment limitation:** native Herdr action children did not inherit the controller's custom `TWIG_AOT1143_STATE_DIR`. An initial custom-root instance stayed Table after a nominally invoked Tree action; exit0 of action invocation was not counted as delivery. Coordinator then used the **default prototype-only namespace** consistently, and `consistent-default-namespace-table`→`observed-default-native-tree-action-effect` proves the samePID27684 changes Table→Tree with Sourcew2:p1. This is not stable-control, store, credential or authority fallback. Production host configuration must explicitly agree on control namespace; do not claim custom-root action support was established.

## Executed behavioral evidence and remaining exercise ladder

The coordinator ran the actual native program and host; this worker inspected their retained artifacts. The selected proof summary is repository-owned. Input injection traverses real installed Herdr/ConPTY, not a fabricated in-process model, but is **not physical keyboard/pointer/font proof**. Evidence identities and limits:

| Contract | Actual record identity / observation |
|---|---|
| Keyboard and restore | `keyboard-select-next`: selection1→2; Enter loads inline detail; `escape-restores-selection-folds-viewport` returns detailnull/selected1/scroll0. |
| Full title and inert HTML | `actual-conpty-home-full-title-frame`, `actual-conpty-sgr-wheel-scroll-effect`, `continuous-html-tail-and-following-row`: TITLE_TAIL, LIST_TAIL, CODE_TAIL, TABLE_TAIL and HTML_TAIL retained; open-right square left/top/bottom visible; no active script content. Native title preserves current SafeText policy, including literal escaped ZWJ. |
| One continuous surface | `actual-conpty-end-continuous-following-bench` moves to later Bench rows through the same scroll; page-up reveals detail HTML tail without a second scroll region. |
| Pointer routing | Historical SGR injection selects#2/folds root#1/scrolls0→3. Subsequent actual Windows GUI pointer events in the corrected Windows Terminal candidate fold/unfold the root, select#2 and scroll0→3, with screenshots and observed mouse counters. Not exhaustive physical-device/font certification. |
| Load-height anchors | `following-row-anchor-before-reload`, `following-row-anchor-loading`, `following-row-anchor-load-height-completion`: first following#2 row stays at visible index3 while scroll67→2→67. |
| Actual resize anchors | attached-client records:53×17→24×17→82×17, following#2 stays visible index3; scroll96→186→92. Bench combining/CJK/joined-emoji/flag chunks wrap as units in captured lines. |
| Late work and cancellation | `closed-detail-drops-noncooperative-late-result`: late1/detailnull; `cooperative-load-cancel-no-visible-resurrection`: canceled1/detailnull; `changed-generation-and-other-item-reject-old-responses`: generation4/detail2/HTML_TAIL_2/late3, no old item resurrected. These are fixture races, not production authority races. |
| Reuse/source | `repeat-open-preserves-existing-view-and-scroll`: PID27684/tree/detail2/scroll3 unchanged; `different-real-source-pane-refuses-same-tab-retarget`: w2:p5 exits1 and original source/UI remains. |
| Hostile control | private-channel-refusals.json: wrongtokenOkfalse, no accepted snapshot; malformed source closes channel(EOF), subsequent live status remains healthy. |
| Closed/stale | `actual-native-close-receipt` exits0; `closed-control-registration-refusal` and `closed-instance-valid-registration-replay-refused` exit1/no live instance. |
| Malformed registry regression | null token initially NRE/exit9; intermediate typed validation still unhandled/exit9; `final-null-token-registration-safe-refusal-green` exits1 contextual refusal. Main verified file unchanged, then explicitly removed only owned dead fixture. |
| Latest final payload | `final-source-valid-registration-launch`→`final-source-valid-registration-existing-ui-table`: samePID42032/sourcew2:p1; `final-source-clean-close` exits0. |
| Standalone cleanup | standalone-restoration.json: actual published native panel exit0; input503→503, output7→7 and outputCP437→437, all equality observations true. Observer is dynamic PInvoke outside the AOT payload, not a JIT application fallback. Main drove j/Enter/full HTML/Q in the native console/ConPTY. |

**Resize diagnosis and rejected patch:** headless server config/divider metadata reported80/38-column layouts while both native program and actual pane-read output still had120 columns. Hypothesis H1 was missing rendering-client PTY-size propagation; H2 was cached inactive STD output handle; H3 was native struct layout. Before republishing any proposed geometry change, attaching the actual isolated Herdr client made the **old running binary with original STD geometry** report53×17 and reflow. Further actual divider mutations gave24 and82-column program/host output. Thus H1 is confirmed for this run, and no app geometry defect was established. The unvalidated CONOUT$/active-handle patch was completely reverted; it is not in final source. Treat layout metadata alone as insufficient terminal resize proof.

Remaining reusable exercise checklist (items already exercised above retain their records; do not silently mark untested edges as covered):

1. **Native build:** record SDK/RID, complete publish diagnostics, output size and native executable evidence. Run the actual published binary, not `dotnet run`. A successful build is only this rung.
2. **Windows Terminal standalone:** record actual terminal version, geometry/font and exit. Navigate wrapped Bench rows with arrows/j/k; test Enter/Esc and left/right/Space folds. Save selection, folds and semantic viewport before detail; close restores them. Inspect title-tail/HTML-tail sentinel fixture text at 80, 40 and about 20 columns, then widen without loss.
3. **Unicode:** fixture contains combining `é`, CJK `中`, joined `👩‍💻`, and regional-indicator `🇳🇿`. Wrap never splits an extended grapheme. Record actual glyph/cell behavior at line boundaries, including terminal/font-dependent width limitations. Native HTML safe-text format escaping stays explicit.
4. **One surface/frame:** full detail follows every wrapped line of its captured Bench row. Only square left/top/bottom rules; no right vertical/corners, no nested independent viewport. Wheel/page motion traverses detail and following Bench rows continuously.
5. **Anchor transitions:** position a following Bench row at a known screen row; complete a loading placeholder into tall detail and shrink/reload it. That row retains its screen anchor when geometrically possible. If the viewport top is within detail, retain semantic block/grapheme offset rather than unrelated line number. Resize narrow/wide and exercise bottom clamping explicitly; impossible screen-row preservation is reported, not hidden.
6. **Cancellation/late result:** start delayed fixture work, close before completion, reopen another item or change fixture generation/view, then let the intentionally noncooperative old result arrive. Old content must not reappear; visible counters/state distinguish canceled from dropped-late work. Repeat while resizing and during external intent delivery. This is not a real ADO race test.
7. **Native Herdr pane:** capture returned host-pane metadata and read visible/ANSI output. Use `herdr pane send-keys <prototype-pane> Enter` and `herdr pane send-text <prototype-pane> j` only on the created prototype; use actual documented resize controls with its explicit ID. Input sent through the real host is distinct from an in-process fabricated event, but still does not prove physical keyboard routing by itself.
8. **Actual mouse/resize:** the user subsequently authorized dedicated foreground Windows Terminal control/screenshots for this session. Actual Windows pointer click/disclosure/wheel and actual window resizing were exercised on coordinator-created windows only. Preserve that bounded authorization and evidence distinction; never target unrelated terminals. Herdr resize proof requires an attached rendering client, not headless logical rectangle metadata alone.
9. **External action/context:** launch from source A, send Table/Tree intents, confirm same PID/host-pane ID and captured source workspace/cwd. A differing source B in the same tab must refuse or require an explicitly separate instance, never silently retarget. Inspect ack and visible mode; an action process exit alone is not proof of UI delivery.
10. **Reuse/close/stale:** repeated open preserves viewport/divider/view; normal close restores terminal/input modes, cursor and alternate screen, and deletes only its registration. Retain a copied fixture registration before close and confirm replay refuses. Test replacement-token cleanup, wrong token, malformed registration and a slow live peer. Separately destroy only a coordinator-created prototype pane and inspect stale-control refusal/recovery. No stable plugin pane is used as a destructive test.
11. **Preservation and review:** independently review report, observed logs and scope; explicitly decide which terminal/platform seams remain. No tracker closure based on compile, source review, fake data or simulated pointer coverage.

## Owner gates, blockers and disposition

- **Start gate resolved:** the original frozen-review `contextId` null→1142 drift receipt remains preserved. User explicitly chose rerun; Main then accepted the same native review and applied its exact digest, obtaining fresh Doing revision5 readbacks before sending START_GATE_OPEN. The worker read kickoff/start-gate-open.json and only then created executable source. No alternate tracker or reviewer route was used.
- **Current evidence gates:** actual Windows Terminal GUI and Windows pointer routing have been exercised after explicit user authorization. The user reported accented/CJK/emoji content appeared correct; screenshots disclose flag missing-glyph fallback and native Show's intentional ZWJ escaping. Initial expansion and Home/End-during-load transitions are corrected and exercised. Universal font/cell-width conformance, Linux/macOS, production visual/authority/data/pull behavior remain bounded or unestablished rather than invented prototype completion conditions.
- **Owner decisions:** production terminal strategy; supported OS matrix; Unicode format sanitization/cell-width policy; old-TUI editing/staging preservation; full Show extraction/parity; latency targets. The prototype cannot silently retire these capabilities or authorize a production cutover.
- **Independent review:** final fresh qualification review accepts AC1–AC5 for the measured fixture slice, and fresh scoped source review accepts the Home/End correction with no remaining finding. Both results are retained separately from historical reviews. Neither judgment supplies native publication authority or changes ADO state.
- **Retention:** complete experimental source/control/manifest retained **opt-in**, not production. No prototype binary was published or registered by this worker. Preserve runtime evidence before any coordinator-owned unlink/close; never touch stable products, defaults or shared data. Source cleanup/disposition does not silently become a product cutover.
- **Stable surfaces:** normal Twig/TUI/Go binaries, installed plugin/action IDs, hotkey, config, shared store, auth and Tasks 139/1113 remain untouched by this worker. No tracker mutation, credential access/copy, production1144 implementation, bug1151/1152 work, commit or push was performed.

## Actual Windows Terminal expansion correction

The user reported that loaded detail jumped to the end of the expanded segment. A throwaway smoke harness exercising the actual `Browser` implementation failed before the fix on initial viewport retention. `Capture` previously prioritized any visible following Bench row even when the opened item was still visible; loading then moved that row down by the complete detail height and scrolled the reading position away. The correction keeps the opened row's viewport when it is visible and retains a semantic anchor across placeholder replacement. Deliberately viewed following-row anchors remain supported; scrolling during loading clears the saved load anchor so input is not overridden.

The same harness passed after correction: initial loading viewport, opened row plus beginning of content, following-row reload anchor, collapse restoration, detail reading position through reload, and narrow reflow. An actual full-trim NativeAOT publish succeeded to the separate opt-in `publish/viewport-fix` output; existing running prototype outputs and all stable products were left untouched.

Coordinator-controlled Windows Terminal screenshots verify the selected wrapped root and beginning of detail remain visible after load (`scroll0`), Page Down traverses content, narrow window resize retains the reading surface, and Esc restores the root viewport (`scroll0`). The yellow footer remains on the last visible content row, clipped horizontally rather than pushed below the viewport. Screenshots and the disposable harness are retained under `C:/Users/dangreen/AppData/Local/Temp/twig-viewport-check`, notably `verified-full-window-loaded.png`, `verified-narrow-reading.png`, and `verified-full-window-collapse.png`. Inputs were sent only after checking the dedicated candidate window's foreground identity; unrelated foreground changes caused refusal, not input to another application.


### Corrected host and actual Windows pointer qualification

The coordinator launched a separately namespaced installed-Herdr session and a temporary manifest pointing directly to the corrected `publish/viewport-fix` executable. The actual host process record confirmed that path. Opening/loading retained `scroll0`; intentional following-row reload retained index3; collapse restored `scroll0`; row click selected2 and wheel advanced scroll3. Evidence: `C:/Users/dangreen/AppData/Local/Temp/twig-viewport-check/herdr-corrected-expanded.json` and `herdr-corrected-interactions.json`. Initial temporary manifest validation refusals were corrected before the candidate proof; an earlier old-payload instance was closed and is not claimed as corrected-source evidence.

In a uniquely targeted dedicated Windows Terminal window, real Windows pointer events (not SGR strings injected into stdin) folded and unfolded the root disclosure, selected child2, and moved the expanded continuous surface from `scroll0` to `scroll3` by wheel. The footer's observed mouse count advanced and it remained on the last visible row. Screenshots: `pointer-fold.png`, `pointer-selected.png`, `pointer-detail-before-wheel.png`, `pointer-detail-after-wheel.png` in the same retained evidence directory. These are agent-generated Windows pointer/keyboard inputs through the actual GUI, supplemented by the user's earlier manual report, not exhaustive human/device/font certification.


### In-flight navigation review correction

Fresh independent source review found that Home/End changed `top` without clearing the pending load anchor. The prior compiled candidate failed the added End-during-load smoke assertion after the original six assertions passed. Home, End and explicit fixture anchor reposition now clear the pending anchor before changing the viewport, matching wheel/page navigation precedence. Final full-trim NativeAOT v2 publication and all eight smoke assertions passed. Actual Windows Terminal screenshots `v2-end-during-load.png` and `v2-home-during-reload.png` show the chosen following viewport (`scroll168`) retained after loading and Home's `scroll0` retained after reload; the footer remains visible in both. These changes do not affect native host control, authority, serializer or terminal-mode restoration code.


### Final qualification and native closeout boundary

Final v2 native Herdr evidence (`herdr-final-v2-navigation.json`) independently corroborates PID34196 and immutable sourcew1:p1: End during loading retains later Bench rows at scroll86; Home during reload retains root and beginning of loaded detail at scroll0. The coordinator observed normal close success, then stopped only its isolated server/client. Fresh qualification and scoped source reviews accept the bounded criteria, with font/Unicode/platform/production seams explicit rather than introduced as extra completion obligations.

The initial type-complete closeout draft and pre-repair refusal remain historical evidence. The defect was repaired in compact native field projection: requested cached field metadata is passed to the shared import-aware resolver only for positively identified captured items (positive revision and lastSyncedAt). Importable known-unset fields become absent; unknown/unimportable/uncaptured fields stay unknown. Twenty-five projection regressions and real NativeAOT smoke passed. A compatible source-pinned isolated build retained the installed target-only export capability without replacing stable binaries or weakening the runner. The fresh combined proposal `.twig/proposals/frontier-close-predecessors-capture-repair.json` was independently accepted with digest `ca4a15d05a26a0d9702ac9a055e0a377bb94dad1ac7400eb486fa2054c16ad72`, applied with all gate fields in one operation per Feature, and refreshed-readback verified Feature1143 Done revision13 on 2026-10-11. This completion does not approve a production toolkit, supported-platform reduction or final cutover.

