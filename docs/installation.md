# Installation

## Supported platforms

The CLI is released for **Windows x64** and **Linux x64** as a self-contained
NativeAOT application. A .NET SDK is needed to build from source, not to run a
release binary. macOS support is suspended on both Intel and Apple Silicon.

Work-item commands require a separately [installed and selected server](server.md#install-the-release-package).
Install the Windows x64 package for Canvas or CLI use; Linux x64 supports
headless CLI commands only. Neither package needs a developer checkout. The
CLI installer does not start a server, change certificate trust or install Canvas.
After selecting an installation, use [`twig server start`](commands/system/server.md)
to start or reuse it. `twig server status -o json` inspects readiness without
starting anything; `twig server stop` explicitly shuts it down.

## Install or update the CLI

Review the installer before executing it from the network.

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

The installers use the latest CLI release and install to `~/.twig/bin`.
Windows updates the user PATH; on Linux, follow the installer's PATH guidance.
Run `twig --version` in the intended terminal to confirm which binary is selected.
Rerun the installer to update. `twig upgrade` and `twig changelog` are retired;
release notes are on [GitHub Releases](https://github.com/PolyphonyRequiem/twig/releases).

## Manual download and verification

Select a CLI `v*` release, not a separately versioned `server-v*` release.
Download the archive for your platform and its matching `.sha256` file from the
same release. Verify **before extracting or running**:

Linux:

```sh
sha256sum -c twig-linux-x64.tar.gz.sha256
```

Windows (PowerShell, in the download directory):

```powershell
$expected = (Get-Content .\twig-win-x64.zip.sha256).Split(' ')[0]
$actual = (Get-FileHash -Algorithm SHA256 .\twig-win-x64.zip).Hash
if ($expected -ine $actual) { throw 'Checksum mismatch' }
```

Extract the complete archive, including its native SQLite library, to a dedicated
directory on PATH. Do not copy just the executable or assume obsolete TUI aliases
are part of the current package. Checksums detect corruption or mismatched
downloads; obtain them from the trusted release, not an unrelated source.

For source builds and local publishing, see [build and release](architecture/build-and-release.md).
For first use after installation, see [getting started](getting-started.md).
