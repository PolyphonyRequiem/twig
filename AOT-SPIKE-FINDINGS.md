# Twig.Tui NativeAOT Spike — Findings

Worktree: `/home/polyphonyrequiem/repos/twig-aot-spike`, branch `spike/tui-aot` (b791f5c0, left dirty — no commits).
SDK: .NET `11.0.100-preview.5.26302.115` (`DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5`).
Package under test: `Terminal.Gui 2.0.0-develop.5185` (lib/net10.0).

---

## Verdict: **HIGH CONFIDENCE YES — with a named, load-bearing caveat**

`src/Twig.Tui` **can** be published as a NativeAOT binary that **actually runs and renders the real
TUI**. I have a 19 MB self-contained native ELF that starts under a tmux pseudo-terminal, draws the
full Twig layout (title bar, `Work Items` tree pane, `Work Item Details` form pane with all field
labels and the `⟦ Save Changes ⟧` button), accepts keyboard input, and exits cleanly with code 0 on
`Esc`. Its rendered output is **byte-for-byte indistinguishable** from the current JIT build's
output, which I captured as a control in the same tmux geometry.

The caveat, which is the actual finding of this spike: the naive AOT configuration **compiles fine
and then crashes at startup**, and the fix is not the one anyone would guess. `Terminal.Gui` runs its
`ConfigurationManager` from a **module initializer** — it executes at assembly load, before `Main`,
unconditionally, and there is **no opt-out switch**. Suppressing IL2026/IL3050 to get a green build
produces a binary that dies with `System.InvalidOperationException: Theme is not a ConfigProperty`
in `StartupCodeHelpers.RunModuleInitializers()`. That is precisely the false-positive failure mode
this spike existed to catch, and I hit it. The working configuration requires **rooting the entire
`Terminal.Gui` assembly from trimming** (`TrimmerRootAssembly` + `TrimMode=partial`), which preserves
the reflection metadata the config manager needs. AOT then works because AOT's problem here was never
the AOT compilation — it was the *trimming* that AOT implies.

So: **yes, and the change is small (~20 lines of csproj), but it is a non-obvious configuration that
must be regression-tested at runtime, not at build time.**

---

## Step 1 — Baseline failure reproduced ✅

```
dotnet publish src/Twig.Tui/Twig.Tui.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:IsAotCompatible=true -p:PublishSingleFile=false \
  -p:PublishTrimmed=true -o /tmp/tui-aot-probe
```

Exactly two diagnostics, both at `Program.cs(72,1)`, nothing else in the project:

```
error IL2026: Using member 'Terminal.Gui.App.IApplication.Init(String)' which has
  'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code. AOT.
error IL3050: Using member 'Terminal.Gui.App.IApplication.Init(String)' which has
  'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. AOT.
```

Confirmed: `Twig.Domain` and `Twig.Infrastructure` built clean, produced **zero** AOT diagnostics.
The entire AOT surface of this project is one call: `app.Init()`.

## Step 2 — What the annotations actually require

Fetched upstream `v2_develop` sources via raw.githubusercontent.com (git clone of that branch fails;
raw fetches work):

| File | Finding |
|---|---|
| `Terminal.Gui/App/IApplication.cs` | **`public IApplication Init (string? driverName = null);` — line 74, NO annotations.** Upstream has already removed `RequiresUnreferencedCode`/`RequiresDynamicCode` from `Init`. The pinned 5185 package predates that removal. |
| `Terminal.Gui/Configuration/SourceGenerationContext.cs` | 65 lines, `internal partial class SourceGenerationContext : JsonSerializerContext`. **It is `internal`.** A consuming app *cannot* extend it or register types into it. There is no public source-gen registration path for consumers. |
| `Terminal.Gui/Configuration/ConfigurationManager.cs` | 844 lines. Has `IsEnabled` / `Enable(ConfigLocations)`. Upstream comment at line 357: *"Reflection-heavy paths are guarded by `RuntimeFeature.IsDynamicCodeSupported` and are dead code under AOT."* — i.e. upstream believes they have handled this. |

**Answer to "is there a supported way for a consuming app to register its types": No.**
`SourceGenerationContext` is `internal` and sealed off. Option 3(a) from the brief is **not available**.

`grep -rn "ConfigurationManager\|ConfigLocations" src/Twig.Tui/` → **NONE**. Twig's TUI never touches
Terminal.Gui's config system. That made option 3(b) — disable it — look attractive.

## Step 3 — Fix attempts

### 3(b) Disable the config system — ❌ NOT POSSIBLE

String-scan of the shipped DLL found `ConfigLocations`, `IsEnabled`, `Enable`,
`SourceGenerationContext`, but **no `FORCE_ENABLE`, no `DisableConfig`**, no MSBuild property and no
AppContext switch. Decisive evidence came from the ILC output itself:

```
ILC : Trim analysis error IL2026: <Module>..cctor(): Using member
  'Terminal.Gui.ModuleInitializers.InitializeConfigurationManager()' which has
  'RequiresUnreferencedCodeAttribute' ...
```

`<Module>..cctor()` is the **module initializer**. `InitializeConfigurationManager()` runs at assembly
load, before any user code, with no way for the consumer to intervene. You cannot opt out of a module
initializer from outside the assembly.

ILC also emitted ~18 further trim-analysis errors *inside* Terminal.Gui itself
(`ScopeJsonConverter<SettingsScope>`, `SchemeJsonConverter`, `AttributeJsonConverter`,
`Scope<T>.GetUninitializedProperty`, and every `SourceGenerationContext.Create_*` factory), plus:

```
Terminal.Gui.dll : error IL3053: Assembly 'Terminal.Gui' produced AOT analysis warnings.
```

**So the upstream claim that these paths are "dead code under AOT" is false for the 5185 build** —
they are reachable from the module initializer.

### 3(c) Suppress the warnings — ✅ builds, ❌ **CRASHES AT RUNTIME**

Added `NoWarn=IL2026;IL3050;IL3053`, `ILLinkTreatWarningsAsErrors=false`, `TrimmerSingleWarn=false`
and a `#pragma warning disable IL2026, IL3050` around `app.Init()`. Result: **clean publish, 12 MB
native binary at `/tmp/tui-aot2/twig-tui`**, `file` confirms `ELF 64-bit LSB pie executable, x86-64
... stripped`.

Ran it in tmux (120x35). **This is the trap, caught:**

```
Unhandled exception. System.InvalidOperationException: Theme is not a ConfigProperty.
   at Terminal.Gui.Configuration.Scope`1.GetUninitializedProperty(String)
   at Terminal.Gui.Configuration.SettingsScope..ctor()
   at Terminal.Gui.Configuration.ConfigurationManager.LoadHardCodedDefaults()
   at Terminal.Gui.Configuration.ConfigurationManager.Initialize()
   at Internal.Runtime.CompilerHelpers.StartupCodeHelpers.RunInitializers(TypeManagerHandle, ReadyToRunSectionType)
   at Internal.Runtime.CompilerHelpers.StartupCodeHelpers.RunModuleInitializers()
```

Dead before `Main`. **A green build here would have been a false positive that sent the 1.0
architecture decision the wrong way.** The trimmer had stripped the `ConfigProperty`-attributed
members that `LoadHardCodedDefaults()` discovers by reflection.

### 3(d) THE FIX — root the assembly against trimming — ✅ WORKS

Diagnosis from the stack trace: the failure is **trimming**, not native codegen. AOT implies
`PublishTrimmed`, so the fix is to exempt Terminal.Gui from trimming while still compiling natively.

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="Terminal.Gui" />
  <TrimmerRootAssembly Include="twig-tui" />
</ItemGroup>
```
plus `<TrimMode>partial</TrimMode>`.

Publish succeeded: **19 MB native binary at `/tmp/tui-aot3/twig-tui`** (vs 12 MB trimmed-and-broken).
The 7 MB delta is the preserved reflection metadata — that is the real, quantified cost of AOT here.

---

## Step 4 — 🔴 RUNTIME PROOF (the decisive test) ✅

`tmux new-session -d -s aot3 -x 120 -y 35 ... /tmp/tui-aot3/twig-tui`, then `capture-pane` after 15s.
**Verbatim captured pane content:**

```
┌┤Twig TUI — PolyphonyRequiem/Twig (Esc to quit)├──────────────────────────────────────────────────┐
│╭┤Work Items├──────────────────────╮╭┤Work Item Details├──────────────────────────────────────────╮│
││                                  ││ ID:            —                                            ││
││                                  ││ Type:          —                                            ││
││                                  ││ Title:                                                      ││
││                                  ││ State:                                                      ││
││                                  ││ Assigned To:                                                ││
││                                  ││ Iteration:                                                  ││
││                                  ││ Area:                                                       ││
││                                  ││ Effort:                                                     ││
││                                  ││ Priority:                                                   ││
││                                  ││ Tags:                                                       ││
││                                  ││ Description:                                                ││
││                                  ││ ⟦ Save Changes ⟧▖                                           ││
││                                  ││ ▝▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▘                                           ││
│╰──────────────────────────────────╯╰─────────────────────────────────────────────────────────────╯││
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

This is not a blank screen or a partial draw. Verified working:

- **The `net` driver initialized** and drove a real pseudo-terminal.
- **Both custom views constructed and laid out** — `TreeNavigatorView` (the 40%-width `Work Items`
  pane) and `WorkItemFormView` (all 11 field labels rendered).
- **Config-dependent rendering survived**: `LineStyle.Rounded` borders (`╭ ╮ ╰ ╯`) are drawn from
  `Glyphs`, a `ConfigurationManager`-owned type. The config system is functioning, not merely
  not-crashing.
- **Twig's own data layer ran**: the title reads `PolyphonyRequiem/Twig`, meaning
  `WorkspaceDiscovery`, `TwigConfiguration.LoadSplit`, SQLite (`SQLitePCL.Batteries.Init()` +
  `SqliteCacheStore`) and the DI container all executed under AOT.
- **Input handled**: `Tab` then `Down` sent via `tmux send-keys` — no crash, pane stable.
- **Clean shutdown**: `Esc` → captured `EXITCODE=0`. No unhandled exception on teardown.
- **Startup**: pane fully rendered when captured at **~400 ms**. (Coarse measurement — tmux polling,
  not instrumented. Not comparable to the CLI's <150 ms budget without proper measurement.)

### Control: JIT baseline

Stashed all spike changes, ran `dotnet run --project src/Twig.Tui/Twig.Tui.csproj -c Release` in the
same tmux geometry. Rendered **identically**. The AOT binary is not a degraded rendering — it matches
the shipping build.

---

## What was done to achieve AOT — the exact diff

`git diff --stat`: **2 files, 19 insertions, 5 deletions.**

**`src/Twig.Tui/Program.cs`** (+2) — pragma around the single call:
```csharp
using IApplication app = Application.Create();
#pragma warning disable IL2026, IL3050 // Terminal.Gui 5185 annotates Init(); upstream v2_develop has removed these.
app.Init();
#pragma warning restore IL2026, IL3050
```

**`src/Twig.Tui/Twig.Tui.csproj`** — replaced the (incorrect) non-AOT block with:
```xml
<IsAotCompatible>true</IsAotCompatible>
<PublishAot>true</PublishAot>
<PublishSingleFile>false</PublishSingleFile>
<SelfContained>true</SelfContained>
<InvariantGlobalization>true</InvariantGlobalization>
<NoWarn>$(NoWarn);IL2026;IL3050;IL3053</NoWarn>
<ILLinkTreatWarningsAsErrors>false</ILLinkTreatWarningsAsErrors>
<WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026;IL3050;IL3053</WarningsNotAsErrors>
<TrimmerSingleWarn>false</TrimmerSingleWarn>
<TrimMode>partial</TrimMode>
...
<ItemGroup>
  <TrimmerRootAssembly Include="Terminal.Gui" />
  <TrimmerRootAssembly Include="twig-tui" />
</ItemGroup>
```

**Invasiveness: LOW on code, MEDIUM on trust.** Zero architecture change, zero rewrite, no change to
the 774 lines of TUI code beyond a 2-line pragma. But it rests on suppressed trim warnings plus a
trimmer-root exemption — the compiler is no longer verifying safety, so **only runtime tests protect
this**. The existing csproj comment "Terminal.Gui v2 beta does not support AOT" is **factually wrong
and should be corrected** regardless of the packaging decision.

---

## Confidence breakdown

**(a) Verified by execution:**
- Baseline: exactly IL2026 + IL3050 at Program.cs:72, nothing else in the project.
- `SourceGenerationContext` is `internal` — no consumer registration path.
- No opt-out for the config manager; it runs from `<Module>..cctor()`.
- Suppress-only AOT **compiles and then crashes** (`Theme is not a ConfigProperty`).
- Root-the-assembly AOT **builds, runs, renders, accepts input, exits 0** on Linux x64, `net` driver.
- AOT rendering is identical to the JIT control.

**(b) Inferred (not executed):**
- The 5185 annotations are stale; upstream `v2_develop` `Init()` has none. Moving to a newer
  Terminal.Gui build would likely let the pragma be dropped. **Not tested — I did not try a newer package.**
- The 7 MB size delta ≈ preserved reflection metadata.

**(c) Untested:**
- Windows and macOS. The Windows driver (`WindowsDriver`, console API P/Invoke) is a genuinely
  different code path and is where I would expect the next failure.
- Non-`net` drivers (`v2win`, `v2net`, curses).
- Mouse input, resize handling, dialogs/modals, and `WorkItemFormView` save round-trip.
- Themes/keybindings loaded from actual JSON config files (only hard-coded defaults were exercised).
- Real startup latency against the <150 ms budget.
- Any behaviour with a populated work-item tree — the pane was empty in this workspace.

---

## Risks

1. **Trim warnings are suppressed, not resolved.** Any Terminal.Gui version bump can silently
   reintroduce a startup crash. This *must* have a smoke test that launches the binary in a pty and
   asserts on rendered output — exactly the test I ran here.
2. **`TrimmerRootAssembly` defeats much of trimming's benefit.** 19 MB vs 12 MB. Still a single
   native binary with no runtime dependency, but not a small one.
3. **Windows is the real unknown** and is the platform most likely to break.
4. **Config-system fragility**: hard-coded defaults work. Themes from JSON files are unexercised and
   are the most reflection-dependent path remaining.

---

## Cost estimate

**Contained, not a rabbit hole — roughly 2–4 days.**

- Config + pragma: ~1 hour (done, above).
- Cross-platform verification (Windows + macOS build & pty smoke test): 1–2 days. **This is the whole
  risk.** If Windows works, ship it; if it doesn't, the wall is upstream and you're back to two binaries.
- pty-based smoke test in CI: ~0.5 day. Non-negotiable given suppressed warnings.
- Try a newer Terminal.Gui to drop the pragma: ~0.5 day, optional.
- Merging TUI into the CLI binary (if one-binary is chosen): separate work, not costed here.

**Recommendation:** the AOT blocker is **real but solvable**, so one-binary is reachable — but do not
bank that decision until the Windows pty smoke test passes. Spend the 1–2 days on Windows
verification before committing the 1.0 packaging architecture. Correct the false csproj comment now
either way.

---

## Reproduction

```bash
export DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5
export PATH="$DOTNET_ROOT:$PATH"
cd /home/polyphonyrequiem/repos/twig-aot-spike
dotnet publish src/Twig.Tui/Twig.Tui.csproj -c Release -r linux-x64 -o /tmp/tui-aot3
tmux new-session -d -s aot3 -x 120 -y 35 "cd /home/polyphonyrequiem/repos/twig-aot-spike && /tmp/tui-aot3/twig-tui"
sleep 15 && tmux capture-pane -t aot3 -p
```

Artifacts: `/tmp/tui-aot3/twig-tui` (19 MB, working) · `/tmp/tui-aot2/twig-tui` (12 MB, crashes at startup — kept as the counterexample).
