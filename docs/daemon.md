# Windows daemon

**Twig owns the daemon.** Its source, build, installation tooling, protocol, and
independently versioned releases live in this repository. The optional
[twig-bench-canvas](https://github.com/PolyphonyRequiem/twig-bench-canvas) package
owns presentation assets and licenses, not the daemon or its durable authority.

The daemon serves same-machine Benches, native item reads, local draft/Plan
workflows, and human-only Save/Apply. It is an optional **Windows x64**
application, separate from the Windows/Linux CLI installer.

## Components and ownership

| Component | Owner and role |
| --- | --- |
| `src/Twig.Daemon` | Twig's executable host; retained assembly/executable name `Bench.Server.exe` |
| `src/Twig.Daemon.Domain` / `.Application` / `.Infrastructure` | Daemon model, use cases, native source/session/transport implementation; retained `Bench.*` assembly names |
| `src/Twig.Daemon.Client` | Pure client library for package verification and same-machine startup, without daemon/provider composition |
| `tools/daemon-package.ps1` | Standalone, verified self-contained runtime packaging |
| `tools/daemon-install.ps1` | Source installation and explicit operator selection; optional sign-in startup |
| `tools/daemon-launch.ps1` | Runtime inventory verification, launch/reuse, and read-only stdio MCP |
| `twig-bench-canvas` | Independently installed presentation package/host/assets |

The daemon can run without canvas assets. Direct static asset routes are optional
diagnostics and serve only verified packages. Normal canvas opens use the separate
presentation host, which can render before daemon readiness and forwards bounded
requests from one browser origin. Closing Canvas does not stop the shared daemon.

## Prerequisites

- 64-bit Windows, running as the intended owner.
- An existing dedicated **private data directory**, separate from the installation.
- An explicit stable localhost port, from 1 through 65535, **not 443**.
- An existing trusted `localhost` server certificate in **CurrentUser\My**, with
  private key, server-auth usage, and its exact 40-character thumbprint.
- For source installation/packaging: a clean exact Git checkout, the SDK selected
  by `global.json`, and an existing Python interpreter.

The tools do **not** create or trust certificates, open remote access, enroll
provider credentials implicitly, or repair machine-wide prerequisites.
Loopback does not mean anonymous access: same-owner Windows sign-in, trusted
pinned TLS, durable daemon identity and protocol checks remain required.

## Install from source

From a **clean** checkout, using an already prepared private directory and
certificate (replace these example values):

```powershell
.\tools\daemon-install.ps1 `
    -PrivateDataDir 'C:\Users\me\AppData\Local\Twig\DaemonData' `
    -Port 7341 `
    -HttpsCertificateThumbprint '<existing-40-hex-thumbprint>' `
    -PythonExecutable python
```

Default installation is `%LOCALAPPDATA%\Twig\Daemon`; the independent canvas root
defaults to `%LOCALAPPDATA%\Twig\Canvas`. `-InstallRoot` must remain a dedicated
directory beneath the current user's LocalApplicationData. Paths must be explicit
local paths, not network shares, drive roots, or reparse-point aliases.

The installer publishes only the daemon, untrimmed and self-contained, under
`versions\<source-sha>`. `runtime.json` inventories file hashes and protocol.
`current.json` selects that runtime together with private-data, port, certificate
and canvas-root settings; a replaced selection is retained in `previous.json`.
Do not hand-edit these records to bypass verification.

Installation starts/stops **no process** and installs no canvas package.
`-RegisterStartup` is explicit opt-in and creates the user's `Twig Daemon.lnk`
sign-in shortcut. This is a per-user process, not a Windows Service installation.
Foreign installation entries or Startup shortcuts are refused rather than replaced.

## Start, reuse, and stop

After installation, run from any directory:

```powershell
twig daemon start -o json
twig daemon status -o json
twig daemon stop -o json
```

Start returns `{"running":true}` only after the selected daemon passes its
authenticated readiness check. Status does not start anything; a stopped
daemon returns `{"running":false}`. A missing installation, an unavailable
locked instance, or a listener whose identity cannot be verified is an error,
not a stopped result. Stop requests graceful shutdown through a per-process
Windows event restricted to the current user, not a PID, HTTP route, or MCP
tool. It confirms shutdown before returning `{"running":false}`. No command
installs, upgrades, force-kills, or changes the sign-in startup selection.

The CLI uses `%LOCALAPPDATA%\Twig\Daemon`. To select a custom installation,
set `TWIG_DAEMON_INSTALL_ROOT` to the same absolute local path under
LocalApplicationData used for `-InstallRoot`. The verified installation record
selects the private data directory; it is not inferred from the current workspace.

Equivalent direct invocation in PowerShell:

```powershell
$launcher = Join-Path $env:LOCALAPPDATA 'Twig\Daemon\daemon-launch.ps1'
& $launcher -EnsureRunning
& $launcher -Status
& $launcher -Stop
```

`-EnsureRunning` verifies the selected runtime, exact Windows owner, trusted TLS,
daemon identity and protocol, then reuses an existing healthy process.
Only actual connection refusal permits serialized startup with the exclusive
instance guard. An existing unavailable process or uncertain startup is not
silently killed, restarted, upgraded, or replayed. Success returns
`{"running":true}`.

For an operator console instead:

```powershell
& $launcher
```

The console prints the localhost endpoint and accepts **`stop`** for explicit
shutdown. Do not start a second console against a running instance's data.
Canvas clients and MCP EOF close their view/session, not this shared process.

To select a newer source build, run the installer from a different clean commit.
Version directories are immutable; reinstalling the same selected SHA is refused.
Selecting new files does not replace a running instance. Coordinate explicit
shutdown and the subsequent launch; do not infer that installation restarted it.
A UI-only package update must not reinstall/restart the daemon or invalidate
compatible sessions.

## Packages and releases

To produce an uninstalled package from a clean checkout:

```powershell
.\tools\daemon-package.ps1 -Output "$PWD\artifacts\daemon-win-x64" -PythonExecutable python
```

The output directory must be new and beneath this checkout's `artifacts`.
Packaging verifies the complete runtime inventory without certificate, Startup,
installation, or process changes.

The [Daemon package workflow](../.github/workflows/daemon-release.yml) packages
Windows artifacts independently of the CLI. `daemon-v*` tags publish
`twig-daemon-win-x64.zip` and its SHA-256 file. This checksum file contains a bare
hex digest, unlike the CLI's filename-bearing `.sha256` files.
Verify the archive before extraction. It contains a runtime directory and the
launcher; it does not invent an operator `current.json`, local data path, or
certificate selection.

For direct package operation, dot-source the included launcher and verify
`runtime.json` and all files with `Get-BenchRuntime`, supplying the exact source
version and the trusted manifest hash from the verified package. Run only the
returned executable with explicit `--data-dir`, `--port`, `--https-certificate`
and optional `--canvas-root`. The supported source installer creates the operator
selection needed by the normal launcher; a downloaded archive alone does not.

## Security and data ownership

The daemon's private data holds durable daemon identity and presentation/native
evidence. User-wide Benches live separately in `benches.db` beside the native
registry. Twig-native items live in explicitly named databases under
`native-databases/` there, not in the daemon directory or a workspace mirror.
Twig connection requests carry a `database` name; `setup/initialize-twig` also
requires `database`. Names use 1-64 lowercase letters, digits or internal hyphens.
Two requests for different database names are different connection requests;
neither an owner-wide default nor the old singleton database is inferred.
The separately packaged presentation client must supply that name.
`TWIG_USER_HOME` selects the metadata home; changing only `-PrivateDataDir` does
not isolate native databases.

Benches retain exact source dependencies, including sources with zero members.
Missing or ambiguous sources produce a degraded view, not fabricated empty
membership. Healthy sources remain usable. Repository bindings can constrain
reads without changing shared Bench intent or draft destinations.

The installed Bench host authenticates the Windows owner and initializes scoped
agent reads, local Plan drafting, pin/unpin and saved-rule editing for the exact
profile/admitted sources. Explicit sign-out and restrictions override this.
An SDK acknowledgement discloses no human content capability and grants no
Save/Apply authority. New connection requests require owner-approved setup;
credentials, accounts, and paths are not chosen by the requesting agent.

Provider/source/principal, profile, policy, revision, digest and native journal
checks still govern changes. **Save/Apply and provider publication are human-only
in the daemon host.** Local staging is not publication; an uncertain result retains
its original capture and attempt without creating another write.

Old developer-local application data is not automatically migrated or adopted.
Use explicitly scoped fresh state when needed, but never delete credentials,
source checkouts, upstream items, or unknown-write evidence as a reset shortcut.

## Managed ADO connection setup

Standalone/global Connections setup does **not** require a source checkout or a
user-supplied local Git folder. After explicit Microsoft enrollment and native
ADO binding, hosts can use `NativeConnectionSetup.InitializeManagedAsync` with
`NativeAdoSetupTarget(organization, project, bindingId, team?, gitProject?)`.
The existing `INativeConnectionSetupGateway.InitializeManagedAsync` forwards the
same target. Organization/project and optional team/Git project retain the fresh
setup semantics; the exact registered binding is mandatory, never an endpoint
default or permission to switch accounts.

The daemon advertises `managed-ado-setup-v1` in its status capabilities.
Owner-authenticated views may call:

```http
POST /api/v1/views/{viewId}/setup/initialize-managed-ado
X-Twig-Bench: 1
Authorization: Bearer <this-view-content-capability>
Content-Type: application/json

{"organization":"example","project":"Project","bindingId":"<exact-native-binding>","team":"Team","gitProject":"Git project","epoch":0}
```

`SetupManagedAdoBody` contains these fields only; `team` and `gitProject` may be
omitted/null, and `epoch` defaults to zero. There is no workspace/store path.
Use the current setup epoch, not a stale navigation value. This is the existing
bounded `202` setup-operation contract: observe the returned `operationId` at
`GET /api/v1/views/{viewId}/setup/operations/{operationId}`, or cancel that
original operation with `DELETE` and `{}`. Observe failures and uncertain effects;
do not automatically resubmit setup after a lost response.

Native code allocates a private context beneath `<native-metadata-home>\sources`
with a private Git anchor as an implementation detail. Git must be available.
`NativeManagedSetupResult.WorkspaceRoot`, and HTTP Ready's
`SetupOperationDto.Source.WorkspaceRoot`, identify that native-owned context for
subsequent reads; clients must not invent, relocate or manage it. Initialization
uses the same exact binding, principal, authority, policy, store creation,
schema and online-read checks as fresh setup. Ready requires successful readback.
Cancellation/failure retains typed native effects and possible partial storage,
with no fallback identity, retry context or tracker publication.

Successful registration appears in the native connection library
(`UserBenchStore.WorkspacesAsync`, exposed by the view's `GET .../benches`).
It can be opened through the returned context without any source checkout.
Registration neither adds Bench membership nor assigns a repository binding,
changes active context, or grants Save/Apply authority.
`InitializeFreshAsync(NativeFreshSetupRequest)` and
`POST .../setup/initialize-ado` remain the separate **repository-scoped** path:
the caller deliberately supplies an existing fresh Git worktree root.
Managed setup does not migrate or adopt developer-local data.

## Read-only MCP

The daemon advertises `mcp-read-v1` on
`POST /api/v1/views/{viewId}/mcp`, on the same authenticated localhost origin.
There is no second listener. This is **not** the retired `twig mcp` /
`twig-mcp` application.

First verify/start the selected daemon with `-EnsureRunning`. Then configure
your MCP client's stdio server with this shape, substituting the **actual absolute**
installed launcher path:

```json
{
  "command": "powershell.exe",
  "args": [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-File",
    "C:\\Users\\me\\AppData\\Local\\Twig\\Daemon\\daemon-launch.ps1",
    "-Mcp"
  ]
}
```

Wrap this object in the server configuration required by your client.
Do not rely on environment-variable expansion in JSON or hardcode a versioned
executable. `-Mcp` and `-EnsureRunning` are separate invocations, not combined flags.
The bridge verifies the existing selected daemon and registers its own scoped
view. Stdio contains MCP JSON-RPC, not operator logs or capability secrets.
EOF closes that view; failed requests are never replayed.

| Tool | Contract |
| --- | --- |
| `read_bench` | Selected Bench members, exact snapshot/item identities, available field references and unavailable-source metadata |
| `show_work_item_details` | Uses returned identities and up to five exact field references; optional `assignedToField`, `focusField`, and explanatory `note`; item values are never caller-supplied |

Details distinguish cached observations, provider revision, unpublished items,
local proposals and unresolved references. Unknown fields, stale snapshots,
revoked access or an unavailable selected source refuse explicitly. An unrelated
missing source does not block a healthy item's details. If no Bench is selected,
choose one in Canvas; MCP does not change selection.

The `ui://twig/work-item-details` MCP App resource belongs to the separately
installed, hash-verified canvas package. Plain text/structured reads do **not**
require those assets; rendering the resource does. No draft, membership, Save,
or Apply tool is exposed by MCP.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Runtime hash/inventory refusal | Check the selected clean package and manifest; do not disable verification or overwrite immutable files |
| Certificate refusal | Check the existing CurrentUser certificate, private key, localhost identity, server-auth usage and trust; the launcher will not alter trust |
| Port/process conflict or unavailable existing instance | Inspect the exact selected operator endpoint/process; do not automatically restart an uncertain instance |
| Degraded Bench | Inspect its admitted source dependencies and provider access, rather than replacing membership with an empty set |
| MCP will not connect | Run `-EnsureRunning` separately; verify the absolute installed launcher path and owner context |
| Details work but the MCP App does not render | Verify/install the independent compatible canvas package; do not reinstall the daemon as a UI repair |
| Unknown Save/Apply outcome | Retain and inspect the original capture/digest/journal; do not repeat publication |

For current implementation boundaries see the
[architecture overview](architecture/overview.md) and
[clean-core contract](architecture/clean-core-contract.md).
