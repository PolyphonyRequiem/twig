# Build and release

## Supported build

[`global.json`](../../global.json) selects the SDK; shared build properties live
in [`Directory.Build.props`](../../Directory.Build.props).
The source currently targets .NET 11; some reusable libraries also target .NET 10.
Use [`Twig.slnx`](../../Twig.slnx) for the actual supported project inventory.
Read the [repository guidance](../../AGENTS.md) before changing code.

The CLI enables NativeAOT, full trimming, invariant globalization, nullable
references, warnings as errors, and source-generated JSON. Release users do not
need an SDK. Do not infer the daemon's packaging from CLI AOT settings: its
Windows runtime is self-contained, untrimmed, and not single-file.

## CLI delivery

[`release.yml`](../../.github/workflows/release.yml) handles CLI `v*` tags.
It waits for successful CI on the tagged commit, builds Windows x64 and Linux x64,
runs release-candidate validation, and publishes platform archives and checksums.
Windows publication requires the approved signing configuration.
macOS builds and qualification are suspended.

Current packages contain the primary CLI and its runtime/native dependencies,
not an active `twig-tui`, `twig-bench-tui`, or `twig-mcp` companion application.
Installers deploy the CLI only. Source copies of retired names or old releases
do not establish present support.
See [installation](../installation.md).

For a source build deployed to your local CLI installation, inspect
[`publish-local.ps1`](../../publish-local.ps1) before running it. Release-style
publication (`dotnet publish src/Twig -c Release -r win-x64` or `linux-x64`)
requires the target platform's NativeAOT toolchain.

## Daemon delivery

This repository owns `Twig.Daemon` and its application/domain/infrastructure/client
projects. [`daemon-release.yml`](../../.github/workflows/daemon-release.yml) packages
it independently on Windows and Linux; `daemon-v*` tags publish the archives and digests.
`eng/daemon-package.ps1` requires a clean exact checkout and writes a verified
package beneath `artifacts`, without installing or changing machine configuration.

`eng/daemon-install.ps1` performs explicit source installation into immutable
version directories. It does not install canvas assets or start/stop a daemon.
The separately maintained `twig-bench-canvas` package owns presentation delivery.
See [daemon operations](../daemon.md) for prerequisites, selection records,
launching, and upgrades.

## Validation

Run builds and tests serially to avoid collisions over build output. From the
repository root, build, run focused regressions for the changed behavior, and
run the offline smoke against the built CLI (60-second default deadline):

```sh
python eng/run-bounded.py --timeout 120 -- dotnet build "-m:1"
python eng/verify-trx.py -- dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --no-build --no-restore -f net11.0 --filter "FullyQualifiedName~SpecificTestClass" --logger "trx;LogFileName=focused.trx" --results-directory artifacts/test-logs/focused
python eng/verify-build.py --binary src/Twig/bin/Debug/net11.0/twig.dll
```

Replace `SpecificTestClass` with the relevant test class.
[`ci.yml`](../../.github/workflows/ci.yml) is the authoritative current required
matrix and selector inventory. Avoid copying test counts or phase-specific
success reports into installation guidance.

Full supported-platform suites run at the release-candidate gate. A successful
routine smoke is not broad-suite, live-provider, or interactive-canvas qualification.
Failed or aborted runs remain failures even if later narrower checks pass.

MinVer derives CLI/package versions from Git tags using the `v` prefix.
Daemon runtime manifests separately bind the exact source SHA and every packaged
file hash. Neither a version string nor a transport acknowledgement is proof that
a work-item mutation was verified.

## GitHub Actions

Pull requests and branch pushes do not start Actions runners. For optional
hosted checks, run `gh workflow run ci.yml --ref <branch>` or
`gh workflow run daemon-release.yml --ref <branch>`. A manual daemon package
run builds artifacts without publishing a release. Official CLI `v*` and daemon
`daemon-v*` tags trigger their respective release workflows.
