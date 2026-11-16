# Contributing

Use the system .NET SDK selected by [`global.json`](../global.json), currently
.NET 11 RC3. The supported project inventory is [`Twig.slnx`](../Twig.slnx).
Read the repository's [agent/contributor rules](../AGENTS.md) before changing code.

## Build and verify

Run builds and tests **serially**; concurrent commands can collide over output.
Routine work uses a supported solution build, a focused regression for changed
behavior, then the offline pre-push smoke:

```sh
python tools/run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools/run-bounded.py --timeout 30 -- bash tools/run-tests.sh --pre-push
```

On Windows, use Git for Windows Bash, not WSL:

```powershell
python tools\run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools\run-bounded.py --timeout 30 -- "C:\Program Files\Git\bin\bash.exe" tools/run-tests.sh --pre-push
```

The smoke uses the already-built CLI, is offline, and has its own hard 15-second
deadline. It does not build, start test hosts, or publish work items.
Documentation-only edits need no application build unless they affect bundled
command help or skills, or have relevant documentation checks.

Use a 60-second external deadline for focused regressions. For example:

```powershell
python tools\run-bounded.py --timeout 60 -- python tools\verify-trx.py -- dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~SpecificTestClass" --logger "trx;LogFileName=focused.trx" --results-directory artifacts\test-logs\focused
```

Replace the selector with the actual focused tests. Reconcile native exits and
TRX results; a `Passed!` line from an aborted host is not success. Do not extend
timeouts or run whole-suite retries as an automatic response to failure.
Complete supported-platform suites are a **release-candidate** gate, not a routine
draft-PR gate. The workflows and scripts, not historical test counts, define the
actual checks.

## Local publishing

`publish-local.ps1` provides source-build deployment to the user CLI installation;
inspect its help/options before replacing your installed binary. Release-style
CLI publication uses `dotnet publish src/Twig -c Release -r win-x64` (or
`linux-x64`) and requires that platform's NativeAOT toolchain.
The daemon has a [separate package/install lifecycle](daemon.md).

## Documentation maintenance

Keep the README introductory. Put setup in user guides, command effects in
`docs/commands`, operator procedures in the daemon guide, and implementation
details in architecture docs. Link to one authoritative explanation instead of
appending another cutover narrative to the README.

Command pages and `docs/commands/index.json` are **embedded in the executable**.
Keep the index, group pages, command pages, and current command declarations
consistent. `Behavior`, `Side effects`/`Effects`, `Exit codes`, and `Failure...`
sections feed leaf help. Changing these docs changes installed help even without
editing C#.

Do not present design plans, old process captures, or historical qualification
results as current supported features. Preserve useful records with explicit
status and links from the [record index](history.md). When a command is retired,
remove its runnable recipe from the public path and give a current alternative,
or state plainly that none exists.
