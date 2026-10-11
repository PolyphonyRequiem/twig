# Feature 1142 — evidence-based AOT TUI replacement contract

## Scope, evidence standing and conclusion

Research-only first frontier of Epic 1141; source baseline `39e10719870437e011567155c328aee03b80c835`. The Go baseline at `C:/Users/dangreen/projects/twig-herdr` and installed products are read-only. This report does **not** select a production terminal library, retire editing, authorize Feature 1144, close Tasks 139/1113, or establish performance targets.

**Execution standing:** the original start review failed immutable acceptance after Bench `contextId:null -> 1142` drift; no publication occurred and its `ready` verdict was not accepted. After user-selected **Rerun native review**, coordinator's fresh frozen-selection native review was accepted, exact-digest publication verified, Starting notes pushed, and1142/1143 refreshed **Doing revision5**. `kickoff/start-gate-open.json` was inspected before executable writes. Coordinator has now prepared/published/executed the isolated native profiler:387 numeric observations,20 warm samples per item/width/mode. This is research evidence, not owner product acceptance or work-item closeout. Original failed evidence remains preserved. [S18,S20,S21]

**Recommended architecture:** one native browser application **module** with complete admitted operations and immutable presentation results; thin CLI, standalone terminal and Herdr **adapters**. The terminal owns input, selection, folds, physical-row layout and viewport anchors. Shared native modules own binding/principal/generation admission, mirror and durable pending-work access, Show presentation and mutations. A terminal event loop can be long-lived without becoming a server or holding an operation lease while idle. This is a recommendation, not a measured latency cure. [S1–S5, S10]

**Important prior-art correction:** the existing `Twig.Tui.csproj` comment saying Terminal.Gui cannot AOT is not reliable evidence. The exact pinned `2.0.0-develop.5185` produced an executed Linux native TUI after rooting Terminal.Gui with partial trimming; naive warning suppression compiled then crashed before Main. Ticket 1002 additionally records owner-run Windows 11 proof on 2026-08-06: 19.2 MB, full old TUI draws, input works, Esc exits 0, four nonfatal IL3051 warnings. Those historical results do not qualify the new Bench UX or current installed Herdr. Task 139's captured live state is still To Do; neither this research nor a prototype implicitly closes it. [S6–S8]

### AC evidence and remaining gates

| Criterion | Evidence delivered / intended measurement artifact | Standing and gate |
|---|---|---|
| AC-1 capability and installation contract | Preserve/replace/owner-gate matrix below; registered action capture; 70-file stable-launch hash inventory | Independently reviewed; editing/retirement dispositions remain explicitly named owner gates, not missing research decisions |
| AC-2 reproducible stage profiling |387 native observations plus12 real current-Go parsed-key→accepted ready stdout frames; reproducible isolated commands, stage/corpus/width/lifetime definitions | Native45/80/120 and Go120-col pipeline executed with real authority and consumer/layout; corrected independent assessment accepted. Physical display/uninjected input transport, OS-cold and approved targets remain explicitly unqualified |
| AC-3 architecture comparison | Runtime/terminal tables and authority invariants below | Recommendation, not a selected terminal or proven persistent-worker need |
| AC-4 decisions and bounded work | Explicit owner decision register and downstream evidence gates | Consequential decisions are named, not silently retired |
| AC-5 preservation |70 stable paths checked unchanged; native logical authority/pending/intents and Bench/unrelated-journal conservation below | Installed paths unchanged; all44 original immutable intents unchanged, pending0→0, same registered principal/binding/default provenance, same Bench settings/exact pins and unrelated Planned journal. Own approved starts/notes/local scope pins/active selection are explicit intentional changes; no credential-blob or whole-filesystem proof |

Research AC1–AC5 were independently accepted, then the mandatory native-backed closeout review accepted the exact proposal digest `ca4a15d05a26a0d9702ac9a055e0a377bb94dad1ac7400eb486fa2054c16ad72`. One batch carried Done and all Feature close fields plus completed outcome. Apply and refreshed readback verified Feature1142 Done revision12 on 2026-10-11. This is bounded research completion, not approval of a production terminal, latency target, retirement or cutover. Receipt: `C:/Users/dangreen/AppData/Local/Temp/twig-viewport-check/predecessors-verified-done.json`.

## Current capability contract (AC-1, AC-4)

Disposition meanings: **Preserve** = observable semantics must survive; **Replace implementation** = change mechanism only after equivalent behavior is proved; **Owner gate** = no retirement or scope reduction without Daniel's recorded decision. Every row is criterion-linked, not an assumption that browser-only is enough.

| Capability | Old Terminal.Gui TUI | Current Go standalone/native Herdr | Disposition and required evidence |
|---|---|---|---|
| Tree navigation and local selection | Active-item ancestry, lazy children, j/k, arrows, Enter expand/collapse, q exit; separate tree/form panes [S9] | Tree/Table, j/k/arrows, folds, page/home/end, clicks/wheel; selection does not call `twig set` [S11] | AC-1 Preserve Bench interaction; replace split-pane implementation with owner-fixed inline layout. Prove wrapped selection, Unicode, narrow columns and mouse parity |
| Full detail and safe HTML | Server-driven `WorkItemDetailDocument`, form-layout fallback, three distinct value states; unsupported contributions are visibly represented [S9] | Native full Show ANSI; selected wrapped row followed by square open-right frame; complete title inside content; one continuous Bench+detail scroll [S1,S11] | AC-1 Preserve complete fields/HTML tails, cached provenance and no executable terminal controls; Enter/Esc restore folds/selection/viewport, keep following-row anchor during resize/loading |
| Field typing and local staging | Sink declares Title/State/AssignedTo persistable; dirty indicator and Save Changes persist atomic batch, reselect overlays saved edits. No direct push in view [S9] | Detail is read-only; Bench configuration editors are not work-item field editors [S11] | AC-1/4 **Owner gate Daniel / Feature 1146**: preserve supported editing/staging or explicitly approve retirement. Banked ticket1003 owner intent includes setting field values; browser success is not permission to erase this |
| State transitions and editing authority | Current save uses pending store batch directly; sink capability limits editability, not ADO ReadOnly metadata; no proof here of gated transition authoring [S9] | Native CLI/proposals own actual publication and process rules | AC-1 Preserve pending work and process-agnostic policy; do not blindly port legacy `twig save` status wording into a new journal-authorized product. Feature1146 must decide end-to-end edit/state workflow |
| Notes, seeds, historical attempts | Shared native persistence is used; dedicated note/seed UI not established from inspected old view source | Browser shows native seed/pending state; seeds explain pin/sync refusal. Review never applies [S11] | AC-1 Preserve data and compatible recovery regardless of UI choice; no auto flush/discard/publish/replay. Dedicated authoring UI remains owner-scoped, not assumed present |
| Review | No Review surface in inspected old TUI | `3`, details/brief, Esc/c exit; latest unresolved proposal or explicit file; captured digest/snapshot; normal Twig remains proposal authority [S11,S12] | AC-1 Preserve read-only Review and exact effects; do not treat viewing as authorization/apply. Separate Feature1146 acceptance |
| Bench pins and automatic membership | No equivalent Bench configuration UI established | Single/Subtree pins independently coexist; inherited/query membership distinct; manual IDs in Pins; uncached IDs remain unverified. No-sprints disables automatic membership [S11] | AC-1 Preserve native settings digest, expected Bench/origin, no remote verification disguised as local configuration |
| Area/sprint configuration | Not established in old UI | Exact/Under area semantics; official configured-team candidates; `@Current`, offsets or absolute sprints; no hidden import of workspace defaults [S11] | AC-1 Preserve process/config-derived rules and explicit confirmation; candidate fetch is distinct from cached read |
| Bench create/switch/delete | Not established in old UI | Named empty create, explicit selection, Default protected, contents-digest deletion confirmation; pending edits untouched; current deletion falls back to Default [S11] | AC-1 Preserve exact names, saved selectors, CAS refusal, no destructive pending-store effect; Feature1145 parity |
| Cache refresh / selected sync | File > Refresh Tree reload; detail form layout can fetch once per type [S9] | R detail cache-only; S exact captured selected root+edge metadata, no related-target materialization or push. Bench `s` separately scopes membership sync [S1,S11] | AC-1 Preserve no implicit presync, distinguish R/S and Bench sync; UI must not replay admitted sync on resize |
| Reconnect and cancellation | Task1113 owns shared binding reconnect work; present old startup only registers connection services, no complete current long-lived host proof [S9,S10] | Ctrl+R acknowledges; authority change clears visible/hidden actor data; generations/cancellation reject late results [S2,S11] | AC-1 Preserve stopped/stale explicit reconnect, same-principal renewal and fresh runtime. Reuse Task1113 contract, do not duplicate or change ownership |
| Standalone `twig tui` and `twig-tui` | Normal CLI uses BinaryLauncher; old companion remains [S13] | Standalone `twig-bench-tui --cwd --view --file` remains [S11] | AC-1 Preserve all existing launch names/defaults until Feature1150 qualified owner cutover; no redirected shims now |
| Native Herdr pane/actions | No old TUI native host integration proved | Registered `twig-herdr` manifest, argv pane entries, reusable control registration; same source context, authenticated local channel; repeated open preserves pane/view [S12] | AC-1 Preserve actual registered IDs/hotkey/reuse/close behavior; replace implementation with same native executable, not Go wrapper. Prototype uses a distinct ID only |
| Install/upgrade/recovery | CLI/TUI install in Twig bin; adjacent companion then PATH; shipped TUI non-AOT, self-contained single-file [S13] | Browser+private native bundle installed independently, checksummed staged replacement with rollback; normal Twig not replaced, explicit migration/reconnect [S14] | AC-1 Preserve deployed-path precedence, retained rollback payloads, data, credentials and user registration. Packaging owner gate remains separate from terminal choice |

### Actual registered and installed paths, not just repository intent

Read-only commands `herdr --help`, `herdr api snapshot`, `herdr plugin list`, `herdr plugin action list --plugin twig-herdr` were exercised. Caller environment had `HERDR_ENV=1`; no focus commands, actions, panes, installs or reloads were invoked. Installed host is `0.9.1-preview.2026-09-29-9dc3a1df2b56`, protocol 22. Captured command output lives in [registered-host-inventory.json](../../tools/research/tui-aot-1142/registered-host-inventory.json).

- Registration inventory says enabled `github:PolyphonyRequiem/twig-herdr@v0.2.4`; actual cached manifest says version **0.4.0**. This discrepancy is **observed**, not evidence that it is broken. Preserve both; coordinator/owner must decide how future update provenance is reconciled without replacing a live host.
- Actual manifest: `C:/Users/dangreen/AppData/Roaming/herdr/plugins/github/twig-herdr-145da1c6cf2a/herdr-plugin.toml`. Stable plugin ID `twig-herdr`; action IDs `open`, `table`, `tree`, `exit-review`, `reconnect`, and each `-windows` counterpart. Panes `bench`, `bench-windows`. Windows commands launch plugin-root `scripts/launch.ps1` then `bin/twig-herdr.exe`; Unix commands directly invoke `./bin/twig-herdr`.
- Actual `C:/Users/dangreen/AppData/Roaming/herdr/config.toml`: `prefix+t` maps to `twig-herdr.open-windows`. No key replacement is authorized.
- Existing native payload lookup is relative to each Go executable: `twig-bench-native/twig-bench-native.exe`, then adjacent `twig-bench-native.exe`, otherwise normal `twig`. Semantic operation expected IDs are **refusal guards, not selectors**. Standalone normal-Twig status and Herdr native binding reads bracket companion operations. Status fingerprints are explicitly not physically atomic native host snapshots. [S2]
- Installed normal Twig families are present in `C:/Users/dangreen/.twig/bin` and `C:/Users/dangreen/.local/bin`, including `.cmd` shims and `twig-core.exe` / `twig-tui-core.exe`; independent Go browser/private companion payloads exist in `.local/bin` and the plugin cache. Source-built baseline payloads exist in `C:/Users/dangreen/projects/twig-herdr/bin`. Inventory includes retained `.old`, `.previous-*`, `.original`, and rollback-directory payloads; none is cleanup fodder.
- Observed `.twig/bin/twig.cmd` and `twig-tui.cmd` walk upward for `.local/bin/<name>.exe`, stop at `.git`, then fall back to adjacent `<name>-core.exe`. The shim therefore differs from native BinaryLauncher's adjacent-then-PATH resolution. **No claim is made about which wins in an arbitrary shell**; PATH and CWD are part of reproduction evidence.

The exact 70 inspected launch/config/manifest/payload file paths, sizes and SHA-256 hashes are in [preservation-before.json](../../tools/research/tui-aot-1142/preservation-before.json), captured before coordinator prototype registration. No credential files or registry rows were inspected/copied to produce this inventory. Hashes can detect changes only for the captured paths, not establish an untouched entire filesystem.

## Native execution-stage map and profiling semantics (AC-2)

### Existing cached detail path

1. CLI parser/offline route setup, input validation and ordinary startup utilities. `--version` exits before most startup work, so its prior ~24 ms is **not** a cached-read startup decomposition. [S3,S13]
2. Composition: workspace discovery; split configuration load; shared connection registrations; optionally Git remote subprocess; network registrations; preliminary store/process-type read to hydrate state/theme; rendering/command registrations. DI registrations are lazy: object/store resolution can occur outside this block. [S3,S4]
3. Store initialization: native worktree operation gate + `MirrorAdmission.Acquire`; SQLite open, collation, WAL/busy timeout, durable store attach/version check, mirror/durable schema checks and generation stamp. More than one store may open. Store-constructor time and OpenWorkspace total are **nested**, and store opens during composition overlap composition. Never sum nested totals. [S5]
4. Initial detail admission: shared bound provider operation lease, repository mirror operation lease, binding resolve + expected-origin refusal, existing current/default Bench check, `ResolveStoredAsync(expectBench)`. The command explicitly refuses absent cached Bench before first-use initialization. [S1]
5. Cache reads: selected item, field definitions, status configuration, children, optional parent, links, pending changes. No selected sync in the experiment. [S1]
6. Layout/presentation: width-configured Spectre console, full Show view construction (`fullContent:true`), safe rich HTML/all HTML-defined fields and complete title, square open-right border, render to StringWriter. This is **native layout+ANSI serialization**, not terminal painting. [S1,S15]
7. Final capture: resolve current binding, compare binding/identity and full `Operation` snapshot, re-resolve captured current Bench before publication. IDs alone do not replace this check. [S1]
8. Envelope/stdout emission; lease cleanup after return. Go separately pre/post-checks native authority, parses JSON, computes physical layout and paints. [S2,S11]

### Why each retained check exists

| Check/module | Failure it prevents | Reuse rule |
|---|---|---|
| Binding resolver and bound principal | Missing/ambiguous/default/pin selection or another account inheriting admitted data | Shared native module, not Go SQLite/auth logic; renew only the same principal |
| Worktree operation gate | Explicit transition racing in-flight observation or write | Hold only during admitted operation; release before idle |
| Mirror admission/generation | Old storage marker/fingerprint/unfinished transition/generation being reused | Revalidate on each operation; retained DB handle is not authority |
| Expected Bench/origin | A gesture captured on one arrangement retargeting another | Capture exact origin/Bench; refuse rather than retry against a new target |
| Initial cached-Bench check | “Read” creating default selectors and discovering remote self identity | Preserve cache-only semantics; initialization is a separate operation |
| Final binding/operation/Bench comparison | Yielding reads/layout finishing after a mutable capture changed | Keep final check even if immutable setup is reused |
| Pending store and protected sync | Sync/rebind losing edits/notes/seeds or relabeling historic unknown outcomes | Durable history/pending is not a disposable read cache |
| Generation/cancellation fence in terminal | Late completion resurrecting a closed detail or former actor | Cancel and refuse stale results; never merge by item ID alone |

Sources [S1,S2,S5,S10]. No claim of a physically atomic transaction spanning registry, config files, attachments and mirrors is made.

### Isolated experiment plan and definitions

The experiment must execute **real native code** in a separately archived source tree with probes, not mocks or direct raw SQLite. Production instrumentation is not authorized; the instrumented snapshot is the non-production harness seam. Coordinator owns publish/run after files stabilize.

- **Process-first (“cold-process”)**: first detail in a newly launched native executable, before reuse warm-up. OS disk cache, antivirus and account/runtime caches are uncontrolled. This is not “OS-cold”, fresh enrollment, or an empty cache.
- **Warm one-shot**: separate native launches after at least one successful same-context detail read. Composition/store/admission still repeat; OS and runtime-owned file caches may be warm.
- **Warm reused-state**: subsequent invocations on the same resolved native command/DI/store instance. Every invocation acquires and disposes actual native admission and repository leases and performs final capture checks. No lease spans the between-invocation delay. First result is excluded from warm percentiles.
- **Native wall**: process create through exit including stream capture. **Native bracketed wall (not full end-to-end)**: normal native `connection status` before + detail native wall + after + JSON decode/origin verification; excludes Go event queue, terminal physical layout and painted frame. The separate actual-Go experiment below measures parsed-key→ready stdout frame; neither clock is physical input-to-display latency.
- Coordinator-approved corpus: cached1141 (longer Epic),1142 and1143 (Features), widths45/80/120. These type/title labels do not establish representative size coverage. Record actual ANSI UTF-8 bytes, plain characters, title length, row count and available HTML-field character sizes. No cache refresh/network item retrieval during measurement. Narrow terminal1/2/10-column behavior belongs to Feature1143.
- Buffered numeric probe observations use a reflection-free Utf8JsonWriter and flush once after native command completion. Logging I/O is outside inner stage clocks but appears in process wall; observation-list bookkeeping remains small instrumentation overhead. Nested composition/store rows remain labeled nested; do not sum them. Runner includes one probes-disabled control per corpus/width cell.
- Native schema checks can write startup metadata on incompatible stores even for a cached command. Use a coordinator-approved already-admitted disposable context or explicitly approved compatible cached context; no archive/copy of credentials, registry identity substitution, destructive migration or raw Go-store shortcut.

The kickoff's earlier bracketed 3.8–3.9 s detail, ~1.6 s outer status checks, 2.2–2.4 s native command and ~24 ms version are **prior-session observations supplied in HANDOFF**, not fresh research measurements. Minimal/JSON similarity is evidence against a simplistic formatting-only story, not an identified cause. Neither AOT/language nor persistent process is established as the cure. The proposed <=250 ms warm p95 read and <=50 ms pure reflow were suggestions only, not approved acceptance targets.

### Observed installed-native cached baseline (coordinator execution)

Coordinator executed installed `C:/Users/dangreen/.local/bin/twig-bench-native/twig-bench-native.exe` from the approved epic worktree, `TWIG_USER_HOME=C:/Users/dangreen/.twig-tui-epic-tracking`, cached1141/1142/1143, width80,3 samples per item, no `--sync`. Exact capture supplied expected Bench1, binding `bind-e6bf6f457d49d26cc09c542d`, identity `id-5d7fbf575b88e492bc95c948`. Numeric source: `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/native-baseline-observed.json`, inspected read-only by this worker. All9 exit0. [S19]

| Cached item | First new-process wall ms (OS cache uncontrolled) | Later one-shot walls ms | ANSI UTF-8 bytes | Plain chars | Emitted80-col rows | Title chars |
|---|---:|---|---:|---:|---:|---:|
|1141|646.769|730.213,685.214|12866|11070|136|79|
|1142|612.781|778.448,848.964|7644|6642|82|70|
|1143|859.951|777.002,668.082|7728|6724|83|53|

This establishes a **612.8–860.0 ms native-wall range on this approved cached context**, not a new p95 target or identified stage cause. Native full-detail sizes are now observed at width80; HTML source sizes,45/120-col layout, reused state, inner stages, outer status brackets and Go paint are not measured by this baseline. It is a different context from the supplied prior3.8s bracketed observation; do not treat that difference as a causal optimization. Three samples per item are descriptive only. The “first” sample is first in this bounded series, not OS-cache eviction or fresh credential state.

### Measurement status and commands

**Executed real native experiment.** Coordinator's successful full series took369.69s and produced387 observations:9 first-process one-shots,180 warm one-shots,9 probes-disabled controls,9 first reuse calls and180 warm reused calls. Same pinned source39e10719, real native DI/store/binding paths, cache-only approved1141/1142/1143, widths45/80/120; no copied credentials or raw database shortcut. Probe runtime is `.NET11.0.0-rc.1.26425.128`, not the sibling terminal prototype's separate GA.NET10 strategy. Numeric evidence is in [results.json](../../tools/research/tui-aot-1142/generated/results/results.json); durable compact [measurement-summary.json](../../tools/research/tui-aot-1142/measurement-summary.json) preserves provenance, executable/result hashes, stage/cell summaries, dimensions and limitations. [S21]

#### Stage observations (milliseconds, pooled180 warm observations per mode)

| Measured stage | Warm one-shot median | Warm reused-state median | Meaning / nesting |
|---|---:|---:|---|
|Composition|86.312|Not repeated|Callback includes preliminary native store/theme read; not total process startup |
|Store opens, per-detail same-name sum|13.571|Not repeated|Nested within composition/leaf resolution; **do not add** to either |
|Store constructors, per-detail same-name sum|7.848|Not repeated|Subset of store opens; **do not add** |
|Leaf service resolution|7.456|Not repeated|Lazy command/dependency creation, includes another store open |
|Initial admission|428.334|293.806|Real provider+repository operation leases, binding/origin and cached Bench capture |
|Cache data reads|1.235|1.616|Selected item, definitions/status/children/parent/links/pending |
|Native layout+ANSI serialization|15.876|19.978|Full Show at requested width; **not** terminal painting/pure terminal reflow |
|Final admission|133.466|142.356|Mutable binding/Operation and Bench recheck retained |
|Envelope/stdout output|0.400|0.385|Numeric probe flush excluded; output pipe captured |
|Detail operation including scope disposal|588.795|476.410|Parent operation clock; contains inner stages, not additive to them |

Medians are distributions, not additive arithmetic; stage/store clocks overlap by design. In this corpus the measured initial/final admission spans are much larger than data reads/native layout; this does **not** identify which binding/registry/Git/lease substep is expensive or permit dropping a final check. Next bounded performance investigation should subprofile those existing native spans in an isolated copy, then deepen reusable immutable setup while preserving mutable final checks and operation leases.

#### Per-cell native wall versus reused operation (20 warm samples each)

|Item / columns|One-shot native median / nearest-rank p95 ms|Reused inner operation median / p95 ms|
|---|---:|---:|
|1141 /45|740.177 /889.780|475.747 /535.104|
|1141 /80|735.666 /940.167|459.021 /651.023|
|1141 /120|761.931 /865.585|522.795 /654.217|
|1142 /45|743.200 /865.478|441.116 /736.230|
|1142 /80|663.624 /794.929|508.290 /740.883|
|1142 /120|671.919 /862.801|396.017 /587.137|
|1143 /45|662.313 /904.203|425.827 /582.875|
|1143 /80|879.915 /1039.290|600.516 /715.043|
|1143 /120|724.420 /948.876|398.709 /534.427|

One-shot native wall includes create/exit/composition/store/pipe capture; reused inner clock excludes those and25ms between-operation gaps. Comparing them demonstrates setup/reuse overhead in this experiment, **not a startup-only causal decomposition**. Reused operations still take hundreds of milliseconds with real initial/final authority. Reuse is not proof that a private worker is necessary; an in-process terminal module can reuse state without a second process/transport. Warm bracketed wall (status before+detail+status after+decode/guard) pooled median **1299.252ms**, range **979.545–2817.422ms**; no Go input queue/layout/paint included. Nine first-process samples range675.920–1028.518ms with OS cache uncontrolled. Nine uninstrumented-probe controls range583.159–1040.912ms; too few/noninterleaved to isolate probe overhead from scheduling variance.

#### Actual detail dimensions

|Item|Native HTML chars / stored field count|Width45 ANSI bytes / physical rows|Width80 bytes / rows|Width120 bytes / rows|Title chars|
|---|---:|---:|---:|---:|---:|
|1141|7086 /11|11956 /218|12842 /135|13157 /93|79|
|1142|4672 /13|6937 /128|7671 /81|8669 /61|70|
|1143|4745 /13|7011 /130|7731 /82|8769 /62|53|

Plain-character counts per width are preserved in the compact artifact. This is one Epic and two related Features, not a broad small/large production corpus; size distribution is observed rather than labeled representative by assumption. Snapshot measurements occurred after reviewed Doing/notes, so dimensions can differ from the earlier installed-native baseline; no claim of byte-identical old-state data is made.

**Environment/qualification limits:** coordinator records prototype final16.45s NativeAOT republish overlapped part of the369.69s series, alongside background Herdr testing. This was not an idle benchmark machine. Sequential (not randomized/interleaved) modes, small20-sample cells and retained OS/file caches prevent causal or product-p95 qualification. Source-specific net11 native evidence does not validate GA.NET10 production cutover or Linux/macOS. No pure-scroll/reflow, terminal draw, authority-change race, disconnect/renewal or recovery experiment is claimed by this profiler. Owner latency targets and Feature1149 qualification remain open. [S21]

Run from the epic worktree; coordinator only. Preparation refuses an existing snapshot; runner refuses an existing result directory to preserve evidence. All generated sources, dependency `bin/obj`, publish files and numeric evidence remain below the owned `generated/` directory; no production build output is used.

```powershell
python tools/research/tui-aot-1142/prepare.py --gate C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/start-gate-open.json
dotnet publish tools/research/tui-aot-1142/generated/source/src/Twig/Twig.csproj -c Release -r win-x64 -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=full -p:MinVerVersionOverride=0.0.0-1142 -o tools/research/tui-aot-1142/generated/publish/win-x64
python tools/research/tui-aot-1142/profile.py --gate C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/start-gate-open.json --samples 20
```

Default result: `tools/research/tui-aot-1142/generated/results/results.json`, with per-process numeric `probe-*.json` and snapshot source-hash manifest. Runner sets exact approved CWD/home and captured expected Bench1/binding/identity; only cached1141/1142/1143 at widths45/80/120 can execute. Stop on refusal/mismatched origin/cancellation, never reconnect or retry against a new target.20 warm samples plus first observation per one-shot/reuse cell are descriptive nearest-rank p95 evidence, not approval of targets. No item text/HTML/auth-response content is persisted.

`prepare.py` copies tracked project references/build resources from the pinned commit into `generated/source`, preserves existing `twig` assembly identity/internal access grants, adds numeric probes and a restricted repetition loop around the **same actual BenchDetailCommand**. It leaves native inner admission/data/final-check logic intact. `SelfUpdater.CleanupOldBinary` and `CompanionStartup.RunFirstRunCheck` are intentionally omitted only in this copy to avoid cleanup/install/download side effects; this is a disclosed difference against installed baseline. Each repeated ExecuteAsync returns after native `using` lease disposal; a25ms idle gap follows, with no held idle lease. The leaf-service-resolution clock exposes lazy construction separately. No production source or old terminal runtime is patched.

**Observed harness attempt:** coordinator's first preparation stopped before publish because the flush insertion anchor `app.Run(args);` matched both offline-skills and real command dispatch. No native build started. The generator was corrected to the exact top-level native run plus following Git-detection declaration while preserving count1 drift detection. Coordinator retains the unsuccessful snapshot as `generated/source-failed-prepare1`; no generated output was edited by the worker. This is a harness-preparation failure, not native/TUI feasibility or latency evidence.

**Executed build:** coordinator's retained `kickoff/profiler-build-proof.json` confirms corrected prepare+publish command above exit0 in129.91s, SDK11.0.100-rc.1.26425.128, RIDwin-x64, PublishAot/PublishTrimmed true, full trimming and MinVer0.0.0-1142. Output includes `Generating native code`; only informational NETSDK1057, no warnings/errors in successful output. Snapshot manifest lists exactly4 changed files: Program, BenchDetailCommand, SqliteCacheStore and new internal ProfileProbe1142. Actual executable SHA-256 `d68742f18deabd55a10a9a6d5b36706a3824d8964abcd6f91e1f5356e67f2ba3` is bound to executed results. This proves this profiling slice, not a production terminal or cross-platform release. [S21]

**Artifact disposition:** keep cited report, scripts, source probe, preservation/registered-host inventories and compact measurement summary as the reproducible research deliverable. `generated/` is explicitly isolated and gitignored; retain raw results/probes, failed preparation snapshot and source-hash manifest locally until independent review/acceptance. No generated executable is installed, canonical route redirected, stable plugin replaced or data migrated. After acceptance, owner/coordinator may remove only these owned generated outputs after preserving required evidence; no delete command against shared data or installed payloads is part of this research.

The approved measurement origin is the existing admitted epic worktree and metadata home above, **not a copied registry or credentials**. The runner must emit local numeric evidence only, reject out-of-corpus IDs and `--sync`, and preserve unrelated stores/launchers. A future cache-schema compatibility surprise is a stop gate, not permission to migrate/rebuild. Terminal input-to-painted-frame and OS-cold proof remain separate even after native stage timing succeeds.

### Independent review correction — actual current Go gesture-to-ready-frame

Independent review correctly identified that native pre/detail/post/decode timing does **not** include `detail.go`229–261 consumer admission/layout or `render.go`46–133 frame write. The387-row native experiment and its accepted raw/compact records are unchanged; this correction adds separate evidence rather than relabeling the native bracket “end-to-end”.

An isolated pinned actual Go source snapshot at `0d3c1e7f7f3b6aa3cb75c0770348f961660d3bca` adds numeric-only probes at these **existing** paths: ultraviolet parsed Enter/R key receipt before event queue, actual key dispatch, existing native pre-authority command, existing cached native detail command+streams, real JSON/origin/control validation, existing native post-authority command, response event queue, accepted consumer detail layout, and completion of the first ready detail frame's actual stdout write. Every native authority/generation/origin check remains intact; no synthetic key handler, mock response, SQL or credential copy. Only approved cached1141/1142/1143 gestures are logged. The private native executable/SQLite DLL are code-only copies into the isolated binary directory so ordinary companion-relative discovery still works. Normal Twig remains actual status authority.

**Measurement endpoints:** start is parsed-key receipt, not the earlier external input-injection/transport timestamp. End is ready stdout frame write completion into ConPTY, not Herdr consuming/compositing the frame, Windows Terminal drawing it, physical-pointer latency or photons. This covers the actual product gesture/consumer/render loop with a named paint-write limitation; it is not physical-display proof. Small warm Enter/R samples under background load are descriptive; no latency targets are approved.

Coordinator-only commands (worker skips build/run/tests/formatters):

```powershell
python tools/research/tui-aot-1142/prepare_go_gesture.py --gate C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/start-gate-open.json
# Build CWD: tools/research/tui-aot-1142/generated/go-gesture-source
$r = 'C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic/tools/research/tui-aot-1142/generated'
$env:GOCACHE = "$r/go-build-cache"
$env:GOMODCACHE = "$r/go-module-cache"
$env:GOPATH = "$r/go-path"
$env:GOTOOLCHAIN = 'local'
& "$r/go-sdk-1.27.2/go/bin/go.exe" build -trimpath -o ../go-gesture-bin/twig-bench-tui-1142.exe ./cmd/twig-bench-tui
# Run from an isolated real Herdr/ConPTY terminal. Probe path must be fresh and absolute.
$env:TWIG_USER_HOME = 'C:/Users/dangreen/.twig-tui-epic-tracking'
$env:TWIG_1142_GO_PROBE = 'C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic/tools/research/tui-aot-1142/generated/go-gesture-1.jsonl'
& 'C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic/tools/research/tui-aot-1142/generated/go-gesture-bin/twig-bench-tui-1142.exe' --cwd C:/Users/dangreen/projects/twig-worktrees/tui-aot-epic --view tree
# Use actual j/k selection, Enter cached detail and R cache refresh, never S.
# Wait for browser-frame-state selectedItemId and each ready-frame-written numeric row.
python tools/research/tui-aot-1142/summarize_go_gesture.py tools/research/tui-aot-1142/generated/go-gesture-1.jsonl --output tools/research/tui-aot-1142/generated/go-gesture-summary.json
```

Source artifacts: [snapshot preparation](../../tools/research/tui-aot-1142/prepare_go_gesture.py), [actual gesture/frame probe](../../tools/research/tui-aot-1142/profile1142_gesture.go), [numeric evidence summarizer](../../tools/research/tui-aot-1142/summarize_go_gesture.py). **Coordinator prepared, built and executed the actual current-Go browser** in a real120×40 ConPTY, navigated actual selected frame IDs, and produced12/12 accepted ready frames: one Enter and three warm R-cache reads for each approved item. No S/sync or mock response. Raw [JSONL](../../tools/research/tui-aot-1142/generated/go-gesture-observed-01.jsonl), [summary](../../tools/research/tui-aot-1142/generated/go-gesture-summary.json) and nonignored [durable gesture summary](../../tools/research/tui-aot-1142/go-gesture-measurement-summary.json) are distinct from the unchanged387 native observations/compact summary. [S24]

|Actual selected item /120 columns|First Enter in running browser ms|Three warm R→ready frame writes ms|Warm R median ms|
|---|---:|---|---:|
|1141|1208.008|1114.898,1144.881,1228.626|1144.881|
|1142|1181.939|1472.747,1195.559,1322.920|1322.920|
|1143|1017.687|1133.224,1309.067,1130.554|1133.224|

These are observed real product-loop durations, not the earlier native bracket reused under a different label. Warm R includes native pre-authority, native cached detail, parse/origin/control validation, native post-authority, response event queue, actual `handleDetailLoaded` consumer/layout and first accepted ready frame output. Warm R median native-detail command spans are665.863/696.974/634.246ms for1141/1142/1143 respectively; corresponding pre-authority medians230.509/277.803/241.913ms and post-authority254.997/254.670/250.889ms. Actual ready draw+stdout write spans0.515–3.160ms across all12 frames. Some observed tiny stages quantize to0 on the Windows Go clock; **zero is below observed clock granularity, not proof of zero work**. Distribution medians are never added to fabricate total.

One Enter in each item group means first open in this running session, **not** process/OS-cold. Three R samples are too few for latency qualification; the JSON nearest-rank p95 is just the maximum of3 and is not a product percentile claim. Only120 columns were exercised in this added Go loop; native corpus45/80/120 remains separately measured. Frame bytes7336/6911/6868 reflect the120×40 written viewport, not full detail-document size. Physical Windows Terminal GUI/emulator composite/pointer evidence remains outside this parsed-input→ConPTY-write clock.

**Build/platform provenance:** coordinator's Go build receipt exit0 uses the stated `go build -trimpath` argv and original pinned source; exactly4 snapshot files changed, native source/checks unmodified. No compiler was on the tool PATH; coordinator downloaded a **temporary owned** Go1.27.2 Windows SDK archive using official metadata `https://go.dev/dl/?mode=json`, checked archive SHA-256 `1314008898bd40df77af4b014f777f08873dbdfbcd3d92308728ee03304fe04f`, and kept GOCACHE/GOMODCACHE/GOPATH under generated/, GOTOOLCHAIN=local, no global install/PATH changes. Baseline go.mod says1.25.0; this compiler difference is disclosed, not claimed bit-identical to the installed Go build. The unchanged code-only native companion SHA-256 is bound in snapshot manifest. [S24]

**Observed driver limit:** coordinator initially sent Esc+j+Enter too quickly; escape/Alt-prefix ambiguity produced no new ready frame. The driver was paced with250ms after Esc and required the actual selected-item frame before Enter; no product code was changed to special-case the input. Browser q was sent at end and numeric summary verified12ready/12gestures. Background/heartbeat scheduling and probe logging still apply; this bounded sample covers the missing actual consumer pipeline but does not approve targets or waive later host/physical-terminal qualification.

### Supplemental native logical conservation (AC-5 review correction)

Coordinator's read-only native evidence `kickoff/logical-conservation-native.json` compares all44 **original immutable settled remote-write-intent rows exactly unchanged**; current count55 legitimately includes this scope's separately authorized starts/notes, with no unsettled current intent. Pending changes remain0 before/after. Registered identity metadata is exactly the same Lavender tenant/principal, effective bindingID unchanged at revision1 with selectionRevision1, and connection-default provenance remains unchanged. This is logical native metadata conservation, not raw store-byte invariance. [S23]

`kickoff/logical-conservation-bench-journal.json` confirms same settingsDigest, areas, sprints, automaticEnabled and exact pin(id,mode) arrangement from the baseline **after authorized local scope pins1142/1143**. Cached pin-state labels correctly move To do→Doing after authorized starts; the product-tracker projection is not erroneously treated as drift. The original unrelated `tui-auth-followups` Planned journal's digest/origin/operations remain exactly unchanged: not reconciled, replayed or discarded. [S23]

Intentional mutations remain plainly scoped: coordinator's approved Feature start publications/notes, local Single pins1142/1143 and active-item selection. No claim that the whole workspace, board or system registry is byte-identical is made. No credential blob was read/copied/hashed, no raw SQL used, no original Microsoft checkout inspected, and same-principal token renewal is not an identity switch. These evidence files supplement, not replace, installed-path conservation and later recovery/authority-race qualification.

## Architecture alternatives (AC-3)

### Runtime/data lifetime

| Option | Authority and process-agnostic behavior | AOT, packaging, maintenance | Decision |
|---|---|---|---|
| One-shot native command behind existing Go browser | Existing real initial/final native checks plus extra status brackets; native process metadata supplies types/states/fields | Already shipped separate Go+native payload and normal CLI authority; repeated composition and layout, protocol/version/stream lifecycle | Preserve deployed baseline until cutover. Profile stages before attributing cost |
| In-process native browser module, same terminal executable standalone/Herdr | Captured runtime evidence and operation leases; immutable results may reflow without read/network; new operations reacquire native guard, typed invalidation clears actor state | No extra read worker/protocol needed; AOT-clean shared modules, source-generated JSON at external seams; locality for CLI/TUI bugs | Recommended direction, contingent on Feature1143 terminal/host proof and Feature1144 race proof |
| Persistent private read worker | Can reuse composition/store but must still admit each operation, fence generation/cancellation and release idle leases; worker is not authority by existence | Adds start/stop/crash/upgrade/version transport and idle process maintenance; AOT possible, not latency proof | Only reconsider after measurement isolates unavoidable repeated setup and simpler in-process reuse fails |
| Generic weak-authority cache / raw Go SQLite | Cannot establish current principal, native migration generation, pending semantics or final admission from item ID/endpoint alone | Duplicates policy and schema; faster-looking SQL would measure the wrong operation | Rejected as shortcut, not an architecture candidate |
| Global daemon/MCP reactivation | Would reopen withdrawal and multi-host lifecycle scope | New global transport/distribution/control problem | Explicitly out of scope; ticket1005 remains an owner decision, not implied by Windows open-file cleanup |

### Terminal/render strategy

| Option | Coverage / AOT evidence | Platform and host costs | What can select it |
|---|---|---|---|
| Current Terminal.Gui5185 with rooted assembly/partial trim | Prior Linux and owner Windows old-form draws/input/Esc; supports widget/editing framework. Suppress-only pre-Main crash, config reflection metadata and documented initializer deadlock are material liabilities [S6–S8,S16] | Toolkit is cross-platform, but driver/input/mouse/resize current-host proof still needed; single-bin rooting changes trimming trust/size. Current full-trim CLI cannot silently inherit the old suppression recipe | Executed representative Bench/editing/native Herdr prototype, audited warning/rooting policy, owner maintenance decision |
| Newer Terminal.Gui version | Current upstream csproj declares IsTrimmable/IsAotCompatible and reflection-disabled JSON; branch source is not pinned-package execution evidence [P2] | API/config evolution and migration burden; must compare exact version and warnings, not “upstream says compatible” | A separate bounded versioned prototype only if needed; no dependency update here |
| Win32 INPUT_RECORD + VT output, native Spectre formatting | Feature1143 sibling's isolated Windows/.NET10 experiment proceeds independently; consult its feasibility report for actual terminal/host proof. This research's.NET11 native profiling is not that proof. Official input records carry key/mouse/window resize, require mode save/restore; reusable RichHtmlRenderer/RenderTree is not full Show [P3,P4,S15] | Windows-specific input adapter; Unicode cell width/grapheme wrapping, clipboard/editing/PTY semantics and Unix raw-input adapters become owned maintenance. Same manifest+argv executable can host Herdr if proved | Feature1143 actual terminal proof and platform/terminal-maintenance owner gate. Do not extrapolate to Linux/macOS |
| VT input parser + platform raw-mode adapters | Potential clean native rendering seam and cross-platform control without config reflection; no executed evidence in this report | Escape-sequence parsing, mouse protocols, resize/signals, paste, Unicode widths and terminal restoration are real product work | Bounded cross-platform prototype if Win32 strategy is insufficient |
| Spectre Live alone | Existing inert renderables are reusable. Official Live warns not thread-safe, unsupported combination with other interactive prompts/status/progress [P5] | Rendering library is not a complete event/input/widget strategy; controlled single-writer layout still needed | Use native presentation, not an assumed all-purpose interactive shell |
| Keep Go terminal as final wrapper | Current interaction baseline useful for parity | Contradicts fixed one-AOT-.NET application/no-Go-wrapper destination | Preservation baseline only, not final selection |

NativeAOT targets specific RID/platform; requires native toolchain, trimming and no dynamic code generation/loading; **cross-OS compilation is not supported**, so qualification builds run on target-OS toolchains. Upstream `IsAotCompatible` activates analyzers, not proof of successful terminal execution. Windows .NET prerequisite is C++ workload; current repo actually targets net11.0 with multi-target native libraries net10/net11, while overview instructions still describe .NET10. Exact source pins matter: SDK global preview5/latestFeature, native bundle pin11.0.100-rc.1.26425.128, Spectre0.57.2, Terminal.Gui5185, SQLite10.0.6/2.1.11, CAF5.7.13. [P1,P6,S7,S14,S17]

## Bounded change map and owner gates (AC-4)

### Recommended downstream slices, not implementation authorization

1. **1143 isolated terminal/host feasibility:** same native executable, source-linked inert presentation, real keyboard/mouse/resize/wrapped rows, anchor restoration/cancellation/late reply, separate manifest and view intent. Windows evidence is Windows-only. Full Show application dependencies remain an extraction gate, not copied presentation logic.
2. **1144 shared native browser module:** move complete current cached detail/membership operations behind a small interface returning captured origin/Bench, generation/revision/freshness/completeness and immutable data/presentation; retain initial/final checks and exclusive-transition exclusion. Reuse binding resolver, mirror and pending services. Two real adapters are CLI and terminal; avoid a shallow pass-through that leaves callers orchestrating auth+store+layout independently.
3. **1145 Bench parity:** independent single/subtree pins, exact/under rules, configured-team candidates, sprint normalization, create/switch/delete guarded by saved contents, all keyboard/mouse behavior. Preserve no-sprints disables auto membership.
4. **1146 field-edit/staging and Review disposition:** named owner decision first; if preserve, route through shared process-agnostic capability/change/journal modules, protect notes/seeds and revision evidence. No UI-owned field list or direct publication ladder.
5. **1147 hosts / 1148 coexistence:** native action/pane reuse/control cleanup and source context; preserve actual stable launch IDs/hotkey, packaging and fallback/rollback behavior until qualified coexistence.
6. **1149 qualification / 1150 cutover:** measured approved latency matrix, platform-specific terminal restoration, data/recovery/version compatibility and owner-approved retirement. No predecessor closure solely to unblock it.

### Decision register

| Gate | Decision owner | Evidence needed before consequential choice |
|---|---|---|
| D1 work-item field editing/staging retained or explicitly retired | Daniel; Feature1146 | Current three-field atomic staging and saved overlay inventory; banked server-driven form/setting-field intent; chosen shared publication workflow and migration/recovery proof |
| D2 terminal strategy and acceptable trim warnings/rooting | Daniel with 1142/1143 independent review | Current pinned executed terminal/host proof, library risk comparison, editing cost and platform scope; no compile-only verdict |
| D3 latency targets and corpus | Daniel | Actual process-first, warm-one-shot, reused stages; real size/width distribution; separate pure reflow and input-to-screen p95 targets; sample counts and machine/runtime evidence |
| D4 platforms / supported hosts | Daniel | Windows Terminal and installed Herdr proof now, native builds/execution on each approved Linux/macOS RID later. Existing Go release matrix includes five platform targets; this is not new .NET proof |
| D5 recovery/storage/version compatibility and live upgrade | Daniel with native binding/pending owners | Compatible store admission, no migration on read, durable journal/pending preservation, explicit reconnect, rollback and retained payloads. Reconcile actual plugin registration0.2.4 vs manifest0.4.0 deliberately |
| D6 packaging/canonical entry points and final retirement | Daniel; Task139 historical packaging scope, Feature1150 cutover | Existing twig/twig-tui/browser/plugin/shim/update compatibility until qualification; measured native size/dependencies; no silent alias/default replacement |
| D7 reconnect ownership/integration | Task1113 / Spec1103 owner via coordinator | Task1113 Doing snapshot preserved; reuse shared principal/generation/reconnect contract and temporary identity-aware host proof, no scope/state takeover |

## Evidence sources

Repository links refer to the inspected baseline; installed absolute paths are separately captured, not portable source URLs.

- **S1** [BenchDetailCommand](../../src/Twig/Commands/BenchDetailCommand.cs), especially39–108; [CurrentBenchResolver](../../src/Twig.Domain/Services/Workspace/CurrentBenchResolver.cs)77–99.
- **S2** Go baseline `internal/panel/binding.go`37–133 and177 onward; `browser.go`91–191; `detail.go`153–189 and211 onward at `C:/Users/dangreen/projects/twig-herdr`.
- **S3** [native Program](../../src/Twig/Program.cs)39–131,139–196,264–270.
- **S4** [TwigServiceRegistration](../../src/Twig.Infrastructure/TwigServiceRegistration.cs)53–168; [NetworkServiceModule](../../src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs)38–67.
- **S5** [SqliteCacheStore](../../src/Twig.Infrastructure/Persistence/SqliteCacheStore.cs)74–205; [MirrorAdmission](../../src/Twig.Infrastructure/Persistence/MirrorAdmission.cs); [ConnectionBindingContracts](../../src/Twig.Infrastructure/Auth/ConnectionBindingContracts.cs)56–87.
- **S6** Historical `git show 0a8c185d:AOT-SPIKE-FINDINGS.md` (complete source read); [spike commit writeup](https://github.com/PolyphonyRequiem/twig/blob/0a8c185d/AOT-SPIKE-FINDINGS.md).
- **S7** [ticket1002](../../wayfinder-1.0/tickets/1002-fold-the-tui-into-one-binary.md)15–44; [owner Windows result](https://github.com/PolyphonyRequiem/twig/issues/359#issuecomment-5209252542). Current policy supersedes ticket1002's historical MCP packaging discussion: MCP withdrawn.
- **S8** [Windows handoff](../handoffs/windows-native-tui-check.md); captured Task139 `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/item-139.json` read in full.
- **S9** [old TUI Program](../../src/Twig.Tui/Program.cs); [TreeNavigatorView](../../src/Twig.Tui/Views/TreeNavigatorView.cs); [WorkItemFormView](../../src/Twig.Tui/Views/WorkItemFormView.cs)195–367; [PendingChangeStoreSink](../../src/Twig.Tui/PendingChangeStoreSink.cs)44–134; [DetailDocumentSource](../../src/Twig.Tui/DetailDocumentSource.cs).
- **S10** [central binding Spec](../specs/centralized-connection-identity-bindings.spec.md)48–59,121–142; captured Task1113 at kickoff `item-1113.json` read in full; [ticket1005](../../wayfinder-1.0/tickets/1005-does-twig-need-a-server.md); [ticket1003](../../wayfinder-1.0/tickets/1003-what-is-the-1-0-tui.md)58–147.
- **S11** Go baseline `main.go`16–88; `cmd/twig-bench-tui/main.go`16–74; `internal/panel/browser.go`, `detail.go`, `configuration.go`, `management.go`, `render.go`, `run.go` (read-only source inspection).
- **S12** Go baseline `main.go`123–175, `herdr-plugin.toml`; actual registered manifest/config and read-only commands preserved in [registered host inventory](../../tools/research/tui-aot-1142/registered-host-inventory.json).
- **S13** [CLI BinaryLauncher](../../src/Twig/Program.cs)1633–1637,2021–2117; [CompanionTools](../../src/Twig.Infrastructure/GitHub/CompanionTool.cs); [Windows install](../../install.ps1); [Unix install](../../install.sh); [release workflow](../../.github/workflows/release.yml); installed `.cmd` shim content inspected read-only.
- **S14** Go baseline `scripts/install.ps1`1–12,140–207, `scripts/launch.ps1`, `scripts/release.py`23–45 and102–137; `scripts/native-source.json` source39e10719/SDK11rc1; [preservation inventory](../../tools/research/tui-aot-1142/preservation-before.json).
- **S15** [SpectreRenderer](../../src/Twig/Rendering/SpectreRenderer.cs)1140–1296 and nested `OpenRightBoxBorder`1302 onward; [RichHtmlRenderer](../../src/Twig/Rendering/RichHtmlRenderer.cs).
- **S16** [Terminal.Gui initializer deadlock prior investigation](terminal-gui-module-initializer-deadlock.md), especially20–24 and71–123. Historical parallel test-host evidence, not a claim about current native prototype hang.
- **S17** [global.json](../../global.json), [Directory.Build.props](../../Directory.Build.props), [Directory.Packages.props](../../Directory.Packages.props), [Twig.csproj](../../src/Twig/Twig.csproj), [Twig.Tui.csproj](../../src/Twig.Tui/Twig.Tui.csproj); Go baseline `go.mod`: Go1.25, ultraviolet20260303, ANSI0.11.7, VT20260920, uniseg0.4.7, go-pty0.2.3.
- **S18** Preserved coordinator receipt: `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/start-review-run/launch-result.json`, read-only inspection confirms `ok:false`, Conductor exit1, stage `accept` snapshot drift, `publication_hold:true`, `ado_publication:not performed`, verdict `incomplete`; exact stage receipt `start-review-run/artifacts/failure-accept.json`. `held-item-1142.json` confirms revision3, State To do, no local changes. `preservation-verified-check.json` confirms checkedCount70/changedCount0. Precise `contextId:null -> 1142` drift and unaccepted child `ready` verdict are coordinator-reported; this worker did not rerun any workflow.
- **S19** Coordinator actual approved cached-native execution: `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/native-baseline-observed.json`, all9 width80 samples exit0; wall/dimensions inspected read-only. No native subprocess or tracker command was launched by this worker.
- **S20** `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/start-gate-open.json`: `START_GATE_OPEN`, accepted rerun receipt, verified apply receipt and refreshed1142/1143 Doing revision5; inspected before executable artifact creation. [Isolated source preparation](../../tools/research/tui-aot-1142/prepare.py), [real native profiling runner](../../tools/research/tui-aot-1142/profile.py), [internal numeric probe](../../tools/research/tui-aot-1142/ProfileProbe.cs).
- **S21** Actual coordinator profiling artifacts: [raw387 observations](../../tools/research/tui-aot-1142/generated/results/results.json), [source/hash/change manifest](../../tools/research/tui-aot-1142/generated/snapshot-manifest.json), [durable compact measurement summary](../../tools/research/tui-aot-1142/measurement-summary.json). `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/profiler-build-proof.json` confirms exact publish command/SDK/full-AOT/native codegen exit0. Coordinator derived-stage capture `kickoff/profiling-observed-summary.json` and workload capture `kickoff/profiling-environment.json` were read in full; per-cell/HTML/runtime rows read from actual raw output. Worktree/executable paths and startup exclusions are recorded; no stage numbers are guessed from source.
- **S22** Final post-experiment preservation receipt `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/preservation-final-check.json`, read-only inspection: checkedCount70, changedCount0, changes[], capturedUtc2026-10-10T01:20:11.9214810Z. Covers the captured stable paths after profiler and isolated prototype host work; not a claim about uncaptured files or credential contents.
- **S23** Coordinator native read-only conservation artifacts `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/logical-conservation-native.json` and `logical-conservation-bench-journal.json`, both read in full. Scope: original44 immutable intents, pending0→0, identity/binding/selection provenance, Bench configuration/exact selector semantics, original unrelated Planned journal; own authorized changes disclosed. No raw credential/store-byte proof.
- **S24** Actual real-ConPTY Go gesture evidence: [12 raw ready-frame records](../../tools/research/tui-aot-1142/generated/go-gesture-observed-01.jsonl), [numeric summary](../../tools/research/tui-aot-1142/generated/go-gesture-summary.json), [isolated Go source/code-payload manifest](../../tools/research/tui-aot-1142/generated/go-gesture-snapshot-manifest.json), [durable gesture evidence](../../tools/research/tui-aot-1142/go-gesture-measurement-summary.json). `C:/Users/dangreen/AppData/Local/Temp/twig-sol61-kickoff-Fyg9FE/go-gesture-build-receipt.json` read-only confirms exact go-build argv exit0. SDK/version/checksum/isolation and250ms driver correction are coordinator-reported, explicitly distinguished from raw timing/source output. [Compact native logical conservation](../../tools/research/tui-aot-1142/logical-conservation-summary.json) preserves booleans/counts/provenance without copying private metadata/credentials.
- **P1** [Microsoft NativeAOT deployment constraints](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), retrieved2026-10-09.
- **P2** [upstream Terminal.Gui v2_develop project](https://github.com/gui-cs/Terminal.Gui/blob/v2_develop/Terminal.Gui/Terminal.Gui.csproj), retrieved2026-10-09; moving branch, not exact pinned-package evidence.
- **P3** [Microsoft input buffer events](https://learn.microsoft.com/en-us/windows/console/reading-input-buffer-events).
- **P4** [Microsoft virtual terminal sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences).
- **P5** [Spectre official Live display limitations](https://spectreconsole.net/live/live-display/).
- **P6** [Microsoft NativeAOT cross-compilation constraints](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile).
