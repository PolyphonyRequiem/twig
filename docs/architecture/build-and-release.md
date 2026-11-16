# Build and release

## Supported build

[`global.json`](../../global.json) selects the SDK; shared build properties live
in [`Directory.Build.props`](../../Directory.Build.props).
The source currently targets .NET 11; some reusable libraries also target .NET 10.
Use [`Twig.slnx`](../../Twig.slnx) for the actual supported project inventory.

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

## Daemon delivery

This repository owns `Twig.Daemon` and its application/domain/infrastructure/client
projects. [`daemon-release.yml`](../../.github/workflows/daemon-release.yml) packages
it independently on Windows; `daemon-v*` tags publish the daemon archive and digest.
`tools/daemon-package.ps1` requires a clean exact checkout and writes a verified
package beneath `artifacts`, without installing or changing machine configuration.

`tools/daemon-install.ps1` performs explicit source installation into immutable
version directories. It does not install canvas assets or start/stop a daemon.
The separately maintained `twig-bench-canvas` package owns presentation delivery.
See [daemon operations](../daemon.md) for prerequisites, selection records,
launching, and upgrades.

## Validation

The [contributor guide](../contributing.md#build-and-verify) defines routine
bounded build, focused regression, and offline smoke commands.
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
