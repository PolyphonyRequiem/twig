---
command: upgrade
group: system
summary: Check GitHub Releases for a newer twig and apply the update, including companion binaries.
stability: stable
mutates: local
---

# `twig upgrade`

Fetches the latest release from GitHub, compares it to the running version
using twig's SemVer comparer, and applies the update by downloading the
platform-specific archive. Windows/Linux synchronizes `twig`, `twig-tui` and
`twig-bench-tui` from the archive's main native image; macOS installs `twig` and
its legacy `twig-tui` companion. When already current, Windows/Linux repairs
missing or mismatched native launch files from the installed main image without
downloading a different image; macOS installs missing legacy companions. MCP is withdrawn and is not discovered or installed. Reach for it
to keep an installation self-updating in place, without invoking `install.sh`
or `install.ps1` a second time.

## Synopsis

```
twig upgrade [-f | --force]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-f, --force` | bool | `false` | Terminate any process holding a peer binary open (for example, a standalone TUI) before applying the update. Without this flag the update fails fast with a diagnostic pointing at the blocking PIDs. |

## Behavior

`twig upgrade` runs the following sequence
(`src/Twig/Commands/SelfUpdateCommand.cs:22-104`):

1. Print the current version (`VersionHelper.GetVersion()`).
2. Call `IGitHubReleaseService.GetLatestReleaseAsync` — a `GET` against
   `https://api.github.com/repos/<repo>/releases/latest` with a
   `User-Agent: twig-cli` header
   (`src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs:25-26,70-72`).
   Any exception is reported as "Failed to check for updates: …" and the
   command exits `1`.
3. If there is no release at all, print "No releases found." and exit `0`.
4. Compare the latest tag to the current version via `SemVerComparer`. When
   the running version is greater than or equal to the latest tag, twig is
   already current. Windows/Linux repairs missing or mismatched native launch files
   from the installed main image, preserving its version and bytes without an archive
   download. macOS installs missing legacy companions from the current release archive.
5. Otherwise, detect the platform RID via `PlatformHelper.DetectRid()` and
   locate the matching asset on the release. If no RID matches, or no asset
   matches the RID, the command exits `1` with "Manual download required."
6. Call `SelfUpdater.UpdateBinaryAsync(assetUrl, archiveName, companionExeNames, ct, force)`.
   Windows/Linux requires the archive's `twig-tui` and `twig-bench-tui` images to
   match its main native image before staging any binary. Older independently built
   TUI archives are refused, leaving the installed native product unchanged.
   On success it replaces the main binary in place and returns per-companion
   installation results, then prints the release body verbatim as "Release
   notes:" followed by "Update complete. Restart to use `<tag>`."
7. If any tracked binary is held open, `SelfUpdater` throws
   `UpdateBlockedException` and `ReportBlocked` prints the offending PIDs
   along with a suggestion to re-run with `--force`
   (`src/Twig/Commands/SelfUpdateCommand.cs:164-184`). Other exceptions
   produce "Update failed: …" and exit `1`.

The command mutates only local state: the on-disk binary, the companion
binaries beside it, and any lock files probed by `FileLockProbe`. It does
not touch the workspace database or the ADO board. `--force` is a
per-process signal; twig does not attempt to unregister services or restart
long-lived hosts on your behalf beyond terminating the blocking process
identified by `FileLockProbe`.

## Examples

Check for and apply an update on a Windows workstation:

```
$ twig upgrade
Current version: 1.4.2
Checking for updates...
New version available: 1.5.0
Downloading twig-1.5.0-win-x64.zip (7834KB)...
  twig-tui.exe installed
  twig-bench-tui.exe installed

Release notes:
- Added `workspace area sync` (#1234)
- Fixed 403 loop after tenant switch (#1240)

Update complete. Restart to use 1.5.0.
```

Force through a running standalone TUI:

```
$ twig upgrade
Current version: 1.4.2
Checking for updates...
New version available: 1.5.0
error: Update blocked — one or more peer binaries are in use by another process.
  twig-tui.exe (held by PID 8721)

$ twig upgrade --force
Current version: 1.4.2
Checking for updates...
New version available: 1.5.0
Downloading twig-1.5.0-win-x64.zip (7834KB)...
...
Update complete. Restart to use 1.5.0.
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| Update downloaded and applied, or already-current run installed all missing companions | `0` |
| No releases published in the upstream repository | `0` with "No releases found." |
| GitHub Releases request failed (network, rate limit, 5xx) | `1` with "Failed to check for updates: …" |
| Platform RID could not be detected, or no asset matched | `1` with "Manual download required." |
| Peer binary was held open and `--force` was not passed | `1` with a blocked diagnostic naming the PIDs |
| Any other download or replace failure raised by `SelfUpdater` | `1` with "Update failed: …" |

## See also

* [`version`](version.md) — inspect the currently installed version.
* [`changelog`](changelog.md) — read release notes without applying the update.
* [System commands group](README.md)
