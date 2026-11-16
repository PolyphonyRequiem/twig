# Build and release

## SDK and artifacts

`global.json` pins .NET 11 RC3 (`11.0.100-rc.3.26522.148`) with `latestFeature`
roll-forward. `Directory.Build.props` sets `net11.0`, nullable, preview language
features and warnings-as-errors. The CLI enables NativeAOT, full trimming,
invariant globalization and source-generated JSON. RenderTree retains .NET 10
consumer support, exercised by `samples/Twig.DetailHost`.

| Delivery | Source |
|---|---|
| CLI `twig-win-x64.zip`, `twig-linux-x64.tar.gz` | `.github/workflows/release.yml`, `v*` tags |
| Windows daemon package | `.github/workflows/daemon-release.yml`, `daemon-v*` tags |
| RenderTree NuGet | Release workflow, only after platform builds succeed |
| Independent presentation host/assets | Separate `twig-bench-canvas` repository |

Infrastructure is nonpackable and is not pushed to NuGet. There are no TUI,
Herdr, OMP, standalone MCP or item-companion release artifacts. Linux arm64 and
macOS are not release targets.

## Release integrity

CLI release waits for green CI on the exact tagged commit, runs full
release-candidate validation, then publishes each supported native image.
Windows publishing requires approved OIDC Trusted Signing configuration and
fails closed if unavailable. `tools/verify-image-trust.ps1` checks the final
image's Authenticode status, exact publisher/profile EKUs, timestamp and complete
source-commit product version. The final image also runs the bounded offline
smoke before archiving.

Per-archive SHA-256 files and the aggregate `SHA256SUMS` ship with each CLI
release. Preserve signing, checksum and source verification when changing
packaging. NuGet publication remains downstream of successful platform builds
because a pushed package version cannot be replaced.

Daemon packages use an exact clean source checkout and complete `runtime.json`
inventory. Install/launch retain local-path and reparse-point guards, immutable
versions, exact manifest checks, trusted TLS and explicit Startup opt-in.
Installation records use strict schema 5, selecting only the current native
runtime without a migration flag. Earlier records are not silently adopted;
choose an explicitly fresh installation root for disposable developer installs.
There are no legacy migration flags or receipt/adoption routes.

## Routine validation

Build the supported solution, run a focused regression for changed behavior,
then run offline smoke:

```sh
python tools/run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools/run-bounded.py --timeout 60 -- dotnet test <focused-project> --no-build --filter <focused-case>
python tools/run-bounded.py --timeout 30 -- bash tools/run-tests.sh --pre-push
```

On Windows use Git's `C:\Program Files\Git\bin\bash.exe`. Build and test commands
run serially. The smoke checks an already-built CLI offline with a hard
15-second deadline, without test hosts or work HTTP. Complete platform suites
are release-candidate gates, not routine draft-PR/pre-push work. Explicit suite
arguments use the bounded no-build discovery/TRX runner and the same nine
current test projects as release-candidate validation.

Reconcile all child outcomes and `TWIG-VERDICT`; a partial “Passed” summary from
an aborted host is not success. Do not expand deadlines, weaken watchdogs or
retry broad suites to conceal a failure.

## Local publishing

`publish-local.ps1` publishes only the current CLI into repository-local
`.local\bin`. The installed `twig.cmd` walks to the nearest Git root, selecting a
local image when present and otherwise the release image. `-Restore` removes
only that local output/current CLI shim and restores its release filename.
Old host shims are not managed or revived.
