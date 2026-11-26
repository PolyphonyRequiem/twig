# Cold cutover qualification: hold installation

**Decision for #741: do not enable installed execution v2.** No signed matched
installation was available. The extracted Windows packages below are evidence
about those bytes in isolated fixture homes, not an activation certificate.
The previous installation was not changed. In particular, refusal by the new
installer cannot stop an already-running old writer.

## Sources and artifacts

Authenticated GitHub heads matched reviewed Twig `41970aa3bab174385486bd55b7bf5c4c8c8b3d28`
and Canvas `0e442c7c95e9414eb8fd114b4d3232522ee777ca`. Canvas still records
`dependencies/twig` at `323c752476b9bc68f0c79b74b3469f67eff758a7`. It is the
archived test daemon, **not** the server shipped in the following ZIP. Twig
integration started at `87f00bd5` (37 commits beyond reviewed main), Canvas at
`6e110065` (8 beyond). Neither starting integration SHA had an Actions run.

| Windows Release archive in `artifacts/741-final` | Source | SHA-256 |
|---|---|---|
| `twig-win-x64.zip` (NativeAOT CLI) | `e198dd93011bdbcbba5dd834e3c9dc4527aedc47` | `AEF3E4629993DBD46EC8A5D41007B79ABB08EF037ED173C33C30138F4CE83470` |
| `twig-server-win-x64.zip` (self-contained, managed payload and operator) | same Twig | `11C387661A51737690CCB82C5927AEA115EAF4286D7250513CE5BF38FB70C5ED` |
| `twig-canvas-win-x64.zip` (selection, host, launcher and browser assets; no SDK plugin) | `e3ec568152fa44617f283faff03173f422ade570` | `8AB8EEC4B3EDDAD18F9AE2BB4C02EDA6B4A3C9F5562056B61B1015A5C86F007F` |

All three extracted executables returned `NotSigned`. Canvas binds asset
manifest `B05685C9E290726BDACBA6872161DF6637C16032D2C5B6987490BC7001D2CC39`
and host manifest `46A97DC12115EE4E8E44EAAD044430520431B7C87F5AD54A085414132498C439`.
An earlier **different** unsigned CLI/server (`26348646`) successfully created
a Bench, seed and retained edit via the unrestricted command worker. Those
pre-interlock archives in `artifacts/741` have CLI/server hashes
`DB5191A5568D10728A000682CF63B8A70DE4E65E7F6B17F14DDB9FAD78677CEA` /
`1036062DE5AD095DDD3CE7FD86EC467D837CD16D0E2A556BEE114AA2DF918ED7`;
its Canvas (`32a136c4`) hash is `66854DA2DD8A3D05D8FEFC52A6B947D28F51F68F467AC93D848CF2C3311C16CC`.
Do not use that smoke as a post-interlock success.

## Writer, key, gate and launcher inventory

| Surface | Writer / conflict boundary | Evidence and unresolved exposure |
|---|---|---|
| CLI dispatch, legacy pipe, direct `--server-command-worker`, `--commands-only`, manual server, lifecycle, MCP | Worker can compose the entire domain graph. Fixture checks canonical private home, server path, port, certificate, selection, server ID, build and boot; holds an OS lease. | Final extracted CLI/server refused non-fixture Bench mutation, worker, start/stop or startup before file creation (`eng/test-cold-admission.py`). Offline help/version/skills and certificate check remain. CLI `status` and `endpoint` inspect the selected operator; they are **not** fresh activation evidence. |
| Windows installation, per-user Startup, current/previous selections, CLI on PATH, developer shim, direct EXE; Linux install and command-only start/stop | Different data-dir selections can address the same metadata home. A per-directory `server.lock` cannot exclude all old processes. | Candidate server installer refuses before selection/Startup changes; candidate launcher refuses operations other than status/endpoint. Developer shim promotion/restore held. Linux candidate operator permits status only; no Linux package qualification here. Pre-existing launchers/shims and already-open processes are outside these interlocks. No reboot, auto-start or rollback simulation. |
| Canvas extension, launcher/host, old already-attached proxy, v1 HTTP registration, Windows profile/enroll/agent and view reads/actions | Boot negotiation must precede protected content or mutation. Browser grant and view capability are not authority to launch a daemon or transfer its lease. | Candidate SDK open refuses before discovery; independent host requires explicit CLI and private fixture selection. Staging is restricted to a new disposable root. Old-proxy fixture returned understood 403/no content; **no installed SDK plugin** was executed. Legacy adapters remain in source. |
| Old reviewed CLI/server, direct legacy worker, historic embedded daemon and its archived installer/launcher | Legacy Start/Stop, foreground, command-only, direct worker, different homes/ports, selected prior runtime, old sign-in and existing open connections must all be inventoried before enabling a new schema. | Reviewed old CLI/server and proxy were rebuilt unsigned from exact pins and exercised against the fixture owner. The embedded daemon has a separate test-only build, operator and selection; not run as a signed product. Refusing old IPC is not fencing the old binaries' independent writes. |
| Setup/refresh/auth, registry, bindings, workspace mirror/imports, Bench definitions, exclusions, pins, calendar and acquisition | Registry/store instance and connection; Bench revision, exact origin, acquisition epoch and grant. SQLite transactions protect local commits; stale reads must not republish after Save/denial. | `NativeCoreExecution.InventoryAsync` **writes** registrations/evidence. `ActivateAsync` caller booleans do not prove an old process stopped. Tests initialize synthetic isolated workspaces; no closed-world inventory of a real installation, unavailable store or outstanding attempt. |
| Update, Plan, retained editor input, local-only edge, receipt/delivery, publication | Original origin/store/actor/revision, digest, submission/attempt; item, graph readers/writers, create destination and all endpoints; mirror version CAS and provider revision. Known create shrinks destination protection to reserved item/graph. Plan captures, transition manifest and dependent Bench generations must settle together before Clean. | `UserBenchStore.Updates`, `NativeCoreExecution`, `ReleasedWorkspacePublication`, `ReleasedPlanPublication`. Restart checks nonterminal admitted plans/claims and publication inventory before recording the boot. Unknown local alias refuses. This is not proof against wholesale loss/replacement, cross-store rewind, or every missing settled reservation. |
| ADO, GitHub, native Twig authority, local presentation | Remote uncertainty is bound to the original attempt; local authority transaction/receipt or terminal rejection must fence late execute. Display must not acquire provider leases; exact grants start empty. | Real provider adapters against deterministic test transport, native SQLite and packaged process where noted below. No live external provider writes and no access to production data. |

## A1-A26 evidence ledger

Every row is **blocked at the signed installation boundary**. P means an
extracted, unsigned process run; S means a source-built test or narrow
deterministic seam. P may use a test startup hook for HTTP or lifecycle cuts.
Browser tests use the actual extracted host/asset selection but import the
adapter from the selected checkout. Neither P nor S means approved for release.

| Case | Witness / remaining qualification |
|---|---|
| A1 | P `Test-ExecutionOwner.ps1`: conflicting selection, matched boot, 30-second suspended owner retains OS lease. No machine-wide writer exclusion proved. |
| A2 | P negative cold admission and missing create claim. No successful signed cold activation or post-cutover proof against independently running old writers. |
| A3 | P `MatchedCliFreezesInput...`: 100 duplicate clients, one action/submission, verified original; four clients at a time. |
| A4 | Same CLI witness drops the reply, restarts, finds by origin and reports ambiguity; S input receipts. No installed profile replacement exercised. |
| A5 | P receipt CAS, disjoint commit and original Plan delivery scenarios; no complete packaged provider-by-provider fault cross-product. |
| A6 | P unknown/known create and missing-fence scenarios; S graph-reader/writer contention. No exhaustive forged evidence or retained Partial matrix. |
| A7 | P interrupted Plan/new boot does not consume unadmitted capture. S authority settlement. No exhaustive installed Closed-proof provenance. |
| A8 | P `DisjointAdoPatchAndLocalEdit...`: 100 bounded HTTP pages, five-second aggregate guard, unrelated authorized page, 30-second provider park. |
| A9 | S `RestartSettlesExactStepWithoutReapplyingOrTrustingOldRelease` at manifest, domain, staged and settled. Not all installed process crash boundaries. |
| A10 | S `WithdrawalBetweenReadAndEnqueueSendsOnlyRefusal` and generation churn; no signed cross-process enqueue race. |
| A11 | S exact-item denial while project permitted; withdrawal persistence failure and restart start closed. |
| A12 | S client event continuity, ready race, renewal/clock tests; P browser and bounded ready frame. Not every network/clock cut at installed plugin. |
| A13 | P ADO subtree, GitHub complete/truncated query, membership and late save/deny/observe; S generation invalidation. |
| A14 | S calendar identity and native query intent; not all wrong team/gap/overlap/offset/stale variants run at package seam. |
| A15 | P reviewed old CLI, server Stop and already-authorized proxy denied without protected content. Rebuilt previous peers, not signed historical distributions. |
| A16 | S released HTTP page reads, generation retry, no mixed pages. No exhaustive no-write/no-provider instrumentation on installed product. |
| A17 | P noncooperative create drain, retained lease/fence and same-original recovery. No supported reset/rewind enabled; no incompatible cold-install proof. |
| A18 | P native, ADO and GitHub parked disjoint update plus local edit. See run results below; not a live-provider qualification. |
| A19 | P pre-dispatch CAS yield/resume, post-restart non-dispatch and changed capture requiring review. |
| A20 | P original acknowledgement, later local edit, restart, DeliveryConflict and no second send. |
| A21 | S cross-mirror/credential conflict and shared policy/graph endpoints; P disjoint effects. Not a closed-world OS gate audit. |
| A22 | P reserved-ID shrink and ADO create-parent queue; no exhaustive local endpoint alias and multi-store topology. |
| A23 | S `SettlingDisjointSecondCommitCannotClearFirstBenchDependencyAcrossRestart`. Not run with all installed component processes. |
| A24 | S SQLite local CAS/tombstone and operation identity. Missing end-to-end before/after crash at atomic local edge + outcome + ledger. |
| A25 | S `NativeProviderTests`: interrupted fence, lost acknowledgement, durable original settlement preventing late execution. No packaged busy/revision rejection across every cut. |
| A26 | S editor persistence and re-review, P original CLI and private-input browser. No complete installed ABA/lost flush/review/reconnect matrix. |

## Observed results

`artifacts/741-final/results/owner.trx`: 26/27 passed, none skipped. The
GitHub disjoint case did not reach the parked request: the external test
hook could not load Shouldly's `DiffEngine` dependency. After resolving
only the test libraries from the runner, `github-final.trx` passed 1/1
against **the same ZIPs** (disjoint update 6.587 s, local edit 9.260 s,
both before release of A). Thus all 27 distinct process scenarios passed
across two runs, not in a single green run. ADO served 100/100 content
pages in 1.799 s; the matched lifecycle/old-peer script separately passed
with an 802-byte status and 23-byte ready frame.

The source-built seams are separate: 355 JS consumer tests, 119 host
assertions, 20 grant/HTTP cases, 11 native-provider cases, 22 Bench store
cases, 42 editor/input cases, 11 core and 8 consumer planning cases.
Two SQLite and six CLI local-relationship cases also passed, including
receipt reopen and tombstone ABA. Initial editor setup refused the shared
system temp location (19 failures); all 42 ran in user-local temp without
relaxing workspace admission. Two older test expectations incorrectly
treated a calendar save as generation-preserving and an uncovered offset
as known-empty. Corrected expectations passed; neither fix changed product
code. Raw TRX files retain both failed and passing runs.

## Release owner's unblock path

### Local developer desktop smoke (not release qualification)

On Windows the desktop test can attach to an explicitly selected, already-running
fixture, without enabling installed writes or using ADO/GitHub. Start with the
existing trusted localhost certificate used for owner qualification. Do not
create a certificate, change trust, or use the installed server root.

```powershell
$env:TWIG_TEST_OWNER_CERT = '<existing trusted localhost certificate thumbprint>'
$env:TWIG_DESKTOP_HANDOFF = Join-Path $env:TEMP ("twig-desktop-" + [guid]::NewGuid() + ".txt")
dotnet test tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj --filter FullyQualifiedName~NativeDesktopOwnerAdmitsOnlyTheSelectedLocalWorkspace
```

The handoff file is created exclusively after the native seed and pinned Bench
are ready. Its single line is the fixture root. This test owns the server and
keeps it alive for at most 20 minutes; it does not register or open a plugin.
Use the companion Canvas developer picker procedure against that root and
the matched CLI. In another shell, after checking the original submission
against the native journal, create the empty `desktop.finished` file inside
that specific root. The test stops the server and removes its own fixture.
The handoff file records a now-expired path and can be removed.

Only the four-line `owner.fixture` selection ending in `native-only-v1` opts
in. The owner requires a fresh boot, pins `work\.twig\core-workspace.json`
for its lifetime, and checks the native provider again before each workspace
operation. Plan commands and legacy Bench acquisition are unavailable. The
pinned TLS status returns `desktopDeveloperMode: "native-only-v1"` and the
existing boot-specific `executionSession`; neither a fixture UUID nor this
string in a local file is evidence of a live owner. Close/reopen the panel
against the same boot; start a new fixture for another test run.

### Signed installation

Supply trusted historic and new exact-source matched artifacts and signer
policy/attestation for the **whole set**, including server managed payload and
operators, Canvas host/assets/extension and the selection manifest. Twig
`.github/workflows/release.yml` has an approved CLI-only release-environment
OIDC / Azure Trusted Signing path. `eng/verify-windows-release.ps1` checks the
expected publisher/profile, EKU, timestamp and source stamp. Server release
builds unsigned archives plus hashes; Canvas consumer CI is not a signing
path. The hosted historical archive guard covers `daemon-win-x64`, not this
server. Hashes and a localhost TLS certificate are not publisher authorization.

Use a disposable isolated qualification installation with verified previous
artifacts and a cold reboot; inventory and disable old launch paths before
selection. Demonstrate failure on live/unreadable/unsettled inventory, prove
old writers cannot affect the new home, exercise all rows on the signed set
and retain evidence of forward-only recovery. Do not use a forced reset,
caller assertions, altered trust, or an unsigned fallback. Preserve the
existing positive release gates: the held installer intentionally cannot
satisfy the previous unrestricted command-worker/installation smoke.
