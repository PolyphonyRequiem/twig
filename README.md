# Twig

Twig is a NativeAOT CLI for explicit work-item reads and changes, backed by a
local Core store and provider-qualified identities. The supported product also
includes a Windows native daemon and the public RenderTree SDK.

## Current delivery

| Surface | Contract |
|---|---|
| `twig` CLI | Windows x64 and Linux x64; static terminal output and structured reads/mutations |
| Native daemon | Windows x64, built from `src/Twig.Daemon`; executable `Bench.Server.exe` |
| Daemon MCP | Read-only `mcp-read-v1` on the existing authenticated localhost origin; optional verified stdio bridge |
| RenderTree SDK | `PolyphonyRequiem.Twig.RenderTree`, with .NET 10 consumer support; `samples/Twig.DetailHost` is the public-boundary consumer |
| Infrastructure | Internal application composition, not a supported or published public SDK |
| Presentation host/assets | Separately delivered by [twig-bench-canvas](https://github.com/PolyphonyRequiem/twig-bench-canvas); not bundled into the CLI or daemon |

macOS and Linux arm64 are not supported release targets. Old full-screen TUI,
Herdr, Oh My Posh, standalone `twig-mcp`, and Copilot item-companion adapters are
retired, not alternate launch routes. Daemon MCP is a separate, current read-only
interface; it does not restore the retired standalone MCP host.

## Install

Windows:

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

Linux x64:

```sh
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

Release archives contain the CLI and its native SQLite sidecar, with debug
symbols where emitted. Releases publish SHA-256 manifests; Windows images are
signed with the approved public-trust profile using OIDC. No companions are
automatically downloaded or launched.

## Start with explicit scope

```sh
twig --version
twig --help
twig init --org contoso --project MyProject
twig show 1234 --refresh -o json
twig process --help
```

ADO initialization requires an enrolled binding. `init` creates only a **fresh**
Core store, normally `.twig/core.db`. Existing or partial state is refused;
there is no migration, adoption, reset or reinitialize route. Developer-local
application data is disposable, but credentials and upstream items are not.

GitHub initialization requires an explicitly selected native `gh` account:

```sh
twig init --provider github --host github.com --repository owner/repo --account selected
```

`twig init --provider twig` uses the user's durable native authority without
ADO/GitHub credentials. Its authoritative `native-authority.db` is separate from
workspace mirrors, drafts and journals.

## Capabilities and boundaries

| Area | Current behavior |
|---|---|
| Reads | Process/schema discovery, cached `show`/`show-batch`, explicit refresh and bounded queries |
| Local context | Active-item selection, qualified navigation/tree, saved Bench definitions and static workspace tables |
| Changes | Typed scalar/field/batch operations, pending/save/discard and local seed lifecycle |
| Proposals | Current version 3 only; file-scoped validate, preview, digest-confirmed apply and journal status |
| ADO | Discovered process rules and close gates; qualified sync, editor, seed-chain and relationship operations |
| GitHub | Qualified issue reads and explicitly supported writes; observed-non-atomic concurrency, no generic CAS guarantee |
| Native provider | Explicit `Twig.Item` schema, scalar revision CAS and authority-ledger-backed creation recovery |
| Native status | Recorded identity/target selection, not fresh authentication or remote capability proof |
| `connection writes` | Inspect-only correlated transport evidence, never execution authority or replay permission |

Provider support is not interchangeable. GitHub refuses unqualified
tree/navigation/editor/chain/sync/context operations and unsupported query
shapes, and limits queries to 100 total candidates. Discover supported fields,
states and relationship kinds from the bound provider instead of assuming ADO
process names or GitHub parity.

Structured output (`json`, `json-full`, `json-compact`, `minimal`, `ids`) is a
machine invocation. Single-item mutations require `--id`; structured `show` and
targeted `tree` reads require a positional ID or `--id`. Do not use the active
item as automation scope. Structured `set`, navigation and seed publication are
refused. Machine workspace reads retain explicit Bench scope and local drafts,
not the interactive active item's relationships.

For changes, discover the installed syntax with targeted help, preview the exact
file, apply only its reviewed digest, inspect the journal, then refresh-read
affected items. A successful command is not proof of a verified remote outcome.
Unknown publication outcomes must never be blindly replayed. See
[proposal guidance](docs/features/proposals.md) and the
[current composition contract](docs/architecture/clean-core-contract.md).

## Native daemon

`tools\daemon-install.ps1` publishes a clean checkout into immutable versions
under `%LOCALAPPDATA%\Twig\Daemon`. Supply an existing private data directory,
explicit non-default port and existing trusted CurrentUser localhost certificate
thumbprint. `-RegisterStartup` is opt-in. The installer neither creates nor trusts
a certificate and never migrates old developer data.

`tools\daemon-launch.ps1 -EnsureRunning` verifies the selected runtime inventory,
Windows owner, pinned TLS, durable daemon identity and protocol before reuse.
Only connection refusal permits serialized startup; uncertain startup or a
running unavailable process must not be restarted or replayed. Canvas close and
MCP EOF do not stop the shared daemon.

`tools\daemon-package.ps1 -Output <absolute-directory>` builds an independently
verified self-contained package. The daemon release workflow publishes
`daemon-v*` archives and checksums independently of CLI releases. Keep the
presentation asset root independent of daemon versions.

The daemon's `mcp-read-v1` interface uses
`POST /api/v1/views/{viewId}/mcp` on the same localhost origin with owner and
presentation-capability checks. For stdio clients, use `powershell.exe -NoLogo
-NoProfile -NonInteractive -File <absolute-installed-daemon-launch.ps1> -Mcp`
after verifying startup with `-EnsureRunning`. The bridge selects its own verified
runtime; never hardcode a version directory. `read_bench` and
`show_work_item_details` expose qualified cached observations, explicit
unavailability and source/policy boundaries. MCP does not draft, change
membership, Save or Apply. The optional MCP App resource requires independently
verified canvas assets; plain structured reads do not.

## Build and verification

Use the system SDK pinned by [`global.json`](global.json): .NET 11 RC3
(`11.0.100-rc.3.26522.148`). The CLI targets `net11.0`; RenderTree and its
DetailHost consumer retain .NET 10 support.

Routine implementation and draft PRs:

```sh
python tools/run-bounded.py --timeout 120 -- dotnet build "-m:1"
# Run a focused changed-behavior regression with a 60-second external deadline.
python tools/run-bounded.py --timeout 30 -- bash tools/run-tests.sh --pre-push
```

On Windows use `C:\Program Files\Git\bin\bash.exe`, not WSL. Run build and test
commands serially. The default/pre-push smoke checks an **already-built** CLI
offline within 15 seconds; it does not build, start test hosts or perform work
HTTP. Complete platform suites are a release-candidate gate, not routine
pre-push work. Explicit suite selections use the same no-build bounded discovery
and TRX reconciliation as release-candidate validation; failures and timed-out
children must remain visible in `TWIG-VERDICT`.

`publish-local.ps1` publishes only the CLI into `.local\bin` and installs the
current CLI shim, preserving repository-local selection and release fallback.
`-Restore` removes only that local build and current shim.

## Documentation

[Documentation index](docs/README.md) separates current operational guidance
from historical ADRs, Wayfinder decisions, research and plans. Use
`twig <command> --help` for executable-matched syntax and `twig --skill` for
bundled agent guidance. Retired commands, `plan` aliases and presentation-only
options are not compatibility APIs.

## License

[MIT](LICENSE)
