# Windows check: can the terminal interface be compiled natively?

**Read this on the Windows machine at:**
<https://github.com/PolyphonyRequiem/twig/blob/main/docs/handoffs/windows-native-tui-check.md>

Nothing needs to be installed to read it. Step 1 clones the repo anyway, after which this
same file is at `docs\handoffs\windows-native-tui-check.md` in the clone.

**Who runs this:** Daniel, on Windows. No agent and no Linux box can — native compilation
cannot cross operating systems.

**Ticket:** [1002 — Fold the TUI into one binary](../../wayfinder-1.0/tickets/1002-fold-the-tui-into-one-binary.md)
is gated on this result. GitHub issue #359.

**Time:** ~10 minutes if the toolchain is already installed. Add 20-30 minutes once, if not.

**What answers the question:** the program **starting up and drawing its interface** in a real
Windows console. **A successful compile is NOT a pass.** On Linux, one configuration compiled
perfectly and then crashed before it ran a single line of its own code. That is the exact trap
this check exists to catch — so do not stop at "it built".

**Either answer is a good answer.** If it works, the two downloads become one. If it doesn't,
the split becomes a deliberate design choice instead of an accident, and we write down the real
reason. Report whatever happens.

---

## Step 0 — one-time prerequisites

Two things must be installed. Check first:

```powershell
dotnet --list-sdks
```

You need **11.0.100-preview.5** or newer in that list. If not, install the .NET 11 preview SDK
(x64) from https://dotnet.microsoft.com/download/dotnet/11.0

Native compilation also needs the Microsoft C++ linker. If you don't already have Visual Studio
with C++ workloads, install **Visual Studio Build Tools** and tick
**"Desktop development with C++"**: https://visualstudio.microsoft.com/downloads/

Symptom if it's missing: the build fails at the very end with a message about `link.exe`
not being found. That is a missing-prerequisite error, not a real result — install and re-run.

---

## Step 1 — get the code (copy-paste the whole block)

```powershell
cd $env:USERPROFILE
git clone https://github.com/PolyphonyRequiem/twig.git twig-aot-check
cd twig-aot-check
git fetch origin spike/tui-aot
git checkout -B aot-check origin/spike/tui-aot
git log --oneline -1
```

Expected last line: `0a8c185d spike(tui): prove Twig.Tui publishes as a working NativeAOT binary on Linux`

**Use this branch as-is. Do not hand-assemble the settings** — it carries deliberate overrides
that the rest of the repo would otherwise fight, and rebuilding them by hand wastes the trip.

---

## Step 2 — compile it natively

```powershell
dotnet publish src\Twig.Tui\Twig.Tui.csproj -c Release -r win-x64 -o $env:USERPROFILE\tui-aot-out
```

First run pulls packages and takes several minutes; native compilation itself is slow by nature.

**Record for the report:**

```powershell
(Get-Item $env:USERPROFILE\tui-aot-out\twig-tui.exe).Length / 1MB
```

Linux produced 19 MB. Today's shipped Windows download is 79 MB.

If it fails here, **capture the full error text** and stop — that alone is a valid result.

---

## Step 3 — 🔴 THE ACTUAL TEST: run it

Open a **real Windows console window** (Windows Terminal or `conhost`), not an editor's embedded
terminal, and not a redirected/piped shell — the thing under test is console handling.

```powershell
cd $env:USERPROFILE\twig-aot-check
$env:USERPROFILE\tui-aot-out\twig-tui.exe
```

**Expect to see:** a full-screen framed interface titled `Twig TUI — ... (Esc to quit)`, with a
left pane labelled `Work Items` and a right pane `Work Item Details` listing field labels
(ID, Type, Title, State, ...), drawn with rounded box-drawing borders.

**The left pane will be empty and that is fine** — a fresh clone has no synced data. I confirmed
this on Linux today with a throwaway clone: empty left pane, everything else drawn, exit code 0.
Empty panes are expected; a drawn frame is the pass.

**You must run it from inside the cloned folder** (the `cd` above). Run it from anywhere else and
it exits immediately saying the workspace was not found — that is a wrong-directory mistake, not
a result.

**A pass looks like:** the frame draws, the labels appear, the rounded corners render.
**A fail looks like:** it exits instantly with an exception (especially one mentioning
configuration or theme), or you get a blank/garbled screen, or nothing but a cursor.

Then:

1. Press **Tab**, then **Down** a couple of times — confirm it doesn't crash.
2. Press **Esc** to quit, then immediately run `$LASTEXITCODE` — it should print `0`.

**Capture a screenshot of the drawn interface.** That screenshot is the evidence.

If it starts but the screen is wrong, that is still informative — capture it.

---

## Step 4 — two supporting details

Rough startup feel (does it appear instantly, or is there a visible pause?):

```powershell
Measure-Command { Start-Process $env:USERPROFILE\tui-aot-out\twig-tui.exe -Wait }
```
(press Esc when it appears; the number includes your reaction time — a ballpark is fine)

The program takes no command-line options, so there is no way to force a different console
back-end from outside — it picks one automatically. Nothing to do here beyond noting anything
unusual you see.

---

## Step 5 — report back

**Where:** comment on GitHub issue #359 <https://github.com/PolyphonyRequiem/twig/issues/359>,
or just paste the five answers into a twig session. The screenshot is the part that cannot be
reconstructed later, so attach it to the issue rather than only pasting it into a chat.

Five things:

1. Did it compile? (yes / no + error text)
2. **Did it draw the interface in a real console?** — screenshot
3. Did Esc exit with code 0?
4. Binary size in MB
5. Anything odd — flicker, wrong characters, slow start

Then clean up whenever you like: `Remove-Item -Recurse -Force $env:USERPROFILE\twig-aot-check, $env:USERPROFILE\tui-aot-out`

---

## What we already know going in

**Verified by actually running it (Linux):** it compiles, starts, draws correctly, takes input,
and exits cleanly at 19 MB. Also verified: the obvious fix compiles cleanly and then crashes at
startup — which is why step 3 exists.

**Inferred, not verified (Windows):** the Windows console back-end ships in the same component
and is selected by ordinary runtime checks rather than the fragile pattern that breaks under
native compilation, so this is *likely* to work. The genuine unknown is Windows' direct
console-API calls, which nothing has exercised.

macOS is equally unverified but lower risk; it is not part of this check.
