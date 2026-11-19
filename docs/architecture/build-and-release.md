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

### Paired distribution readiness

Source-free Windows Canvas operation needs **both** an endpoint-aware CLI and
the independent Windows daemon archive. CLI endpoint discovery first entered
source in `afbd71e212d0b24be21f92861d2b1e9ea31004f4`; a release number alone
is not evidence that it contains that commit. Check the actual release tag and
assets before claiming an available compatible pair.

| Boundary | Supported contract |
| --- | --- |
| Windows CLI | `twig server endpoint -o json`; bounded origin, certificateThumbprint, selectionId and nullable daemonId; `server start/status/stop` for lifecycle |
| Windows daemon | Self-contained `win-x64`; `twig-bench-service-v1`; runtime manifest schema 1 and operator selection schema 4 |
| Linux CLI/daemon | Self-contained `linux-x64`; headless `twig-command-v1`, owner-only IPC/control; no Canvas HTTPS endpoint |
| Version binding | CLI MinVer `v*` tags; independent daemon `daemon-v*` tags; daemon runtime version is an exact source SHA with a closed file-hash inventory |

Process/type/field/state models remain data, not compatibility version numbers.
No CLI executable is permitted in a daemon runtime; its managed `twig.dll`
dependency is currently required by the daemon command worker and is not an
independently installed CLI. Neither archive supplies Canvas assets.

For an uninstalled Windows candidate pair, from a clean commit:

```powershell
.\eng\daemon-package.ps1 -Output "$PWD\artifacts\candidate\daemon-win-x64" -PythonExecutable python
python eng\run-bounded.py --timeout 120 -- dotnet publish src\Twig\Twig.csproj -c Release -r win-x64 --self-contained true "-m:1" --verbosity minimal -o artifacts\candidate\cli-win-x64
if ($LASTEXITCODE -ne 0) { throw 'CLI candidate publish failed' }
Compress-Archive -Path artifacts\candidate\cli-win-x64\* -DestinationPath artifacts\candidate\twig-win-x64.zip
$hash = (Get-FileHash artifacts\candidate\twig-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  twig-win-x64.zip" | Set-Content -Encoding ascii artifacts\candidate\twig-win-x64.zip.sha256
.\eng\test-daemon-package.ps1 -Archive artifacts\candidate\twig-daemon-win-x64.zip -CliArchive artifacts\candidate\twig-win-x64.zip -PythonExecutable python
```

The verifier extracts both archives into disposable, explicit test roots. The
installer fixture substitutes the external-command boundary before invocation:
no certificate lookup, trust change, shared installation or Startup entry occurs.
The actual apphosts communicate through an isolated commands-only daemon worker
with a private metadata home; only local synthetic work items are changed.
This covers package/IPC compatibility, not positive Windows TLS/owner trust.
Existing `DaemonLauncherTests` cover mocked cold/warm readiness, concurrency,
identity refusal, no-replay cancellation and selected-process stop. Synthetic
upgrade/previous-record checks are not a live installed upgrade or rollback.

The daemon workflow builds and retains both candidate archives separately on
each platform and runs this gate before daemon publication. Linux additionally
tests extracted-package installation and isolated headless lifecycle.
Do not treat unrun platform jobs, synthetic certificate checks or a candidate
artifact as a released/trusted installation.

**Publication is a separate, explicitly authorized operation.** After review,
merge and release approval, choose unused versions and an exact reviewed commit.
The operator commands are:

```powershell
git tag "v$ApprovedCliVersion" $ReviewedCommit
git tag "daemon-v$ApprovedDaemonVersion" $ReviewedCommit
git push origin "refs/tags/v$ApprovedCliVersion" "refs/tags/daemon-v$ApprovedDaemonVersion"
```

These commands publish via the existing tag workflows; do not run them for
build-only validation. They require repository tag-write permission and enabled
Actions; release jobs require `contents: write`. CLI release also requires the
`release` environment approval/configuration, approved Azure Trusted Signing
OIDC access (`id-token: write` and the three `AZURE_SIGNING_*` variables), and
`NUGET_API_KEY` for its library publication job. A successful daemon release
alone does not satisfy CLI signing or publish the candidate CLI. Verify both
releases' checksums, source commits and assets afterward. Trusted Windows
endpoint/owner qualification and real install/upgrade/rollback remain separate
operator-approved checks; never infer them from these build-only tests.

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
