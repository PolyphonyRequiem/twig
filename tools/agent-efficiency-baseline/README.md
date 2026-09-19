# Agent-efficiency baseline — AB#878

This is a diagnostic workload, **not a downstream fix or a new presenter**. It exercises actual Twig command, process-gate, executor and journal code with sanitized offline inputs. Fake ADO services deliberately return controlled snapshots; their results are never described as live ADO verification.

## Run

From the repository root, with Python 3, Bash (for the existing guarded test runner), and the repository's .NET SDK:

```sh
python3 tools/agent-efficiency-baseline/run.py --output artifacts/agent-baseline/run-001
```

Output directories must be new. The command builds the source and runs the fixtures, preserving build logs, complete inputs/results, source assembly hash/version, runtime, per-case classifications and UTF-8 byte/Unicode character counts. It never writes a production work item. The project is diagnostic-only and is not shipped with the CLI.

The command also runs `tools/run-tests.sh Infrastructure`, preserving its exit status and verdict. This reuses the actual internal process-rule gate's supplier/unsupplied-field regressions rather than mocking a gate result or exposing production internals to the diagnostic project.

To also measure a deployed executable:

```sh
python3 tools/agent-efficiency-baseline/run.py --output artifacts/agent-baseline/run-002 --installed /absolute/path/to/twig
```

The installed and source CLIs each get a **different temporary workspace and empty credential home**. Only `version`, `show --help`, cache-only `show 42`, and cold `process Frobnicator` run there. The executable is hashed before and after and never installed or upgraded. Cache versions are read through read-only SQLite connections. No SQL writes or copied production caches are used. Temporary fixture workspaces are deleted; complete output remains in the requested evidence directory.

Never alternate old/new executables against an operational cache. The September 16 capture found installed mirror schema **14** versus source **16**; `SqliteCacheStore.EnsureSchema` rebuilds the disposable mirror on version inequality. Durable proposal/pending data is independently versioned. This explains why unlike binaries cannot provide a valid same-cache payload comparison; AB#688 retains ownership of context retention, not this tool.

## Interpret the verdict

- Default exit **0** means fixtures completed, safety controls held, and the existing Infrastructure suite passed. It does **not** mean all desired product behavior is implemented. Known defects remain visible in the JSON and terminal summary.
- `--require-fixed` returns **1** when any desired outcome remains unmet. This provides a red-capable comparison for subsequent implementation; never change a defect's desired outcome just to make it pass.
- Any failed safety control returns **1** in either mode. Material changes, failed clears, stale reads and unsupplied required fields must not become verified.
- Usage errors and attempts to overwrite a report return nonzero. Build failure/timeouts retain their output and are not fixture success.

`observations.json` carries the complete synthetic request and response for every fixture. `summary.json` describes actual subprocesses and links stdout/stderr files. `input` and `output` inside an observation are JSON strings, not truncated previews. Fixture return values, diagnostics and fake-service call counts belong to that offline run. A fixture invocation is neither an external CLI process nor an outer model-tool call.

Metrics distinguish UTF-8 bytes, Unicode code points, subprocess executions, fixture invocations and outer model calls. Token counts and unknown outer-call counts are **null**, never estimated from characters. The wrapper adds no tokenizer dependency. Assembly hashes identify a build, not a signed release. Do not compare a cold empty result to a warm populated result as a saving.

The per-case output size measures the complete serialized fixture-output envelope; nested `stdout` retains the exact command response. `summary.json` separately measures actual subprocess output. Neither size is an outer model-visible token count.

## Workload and limits

The read workload covers uncached explicit-ID refresh, empty/incomplete catalogs versus genuinely unknown fields, selected/explicit item detail, missing batch IDs and process discovery. The write workload uses the existing semantic readback and lifecycle policy for supported HTML/identity normalization, closing-tag whitespace, material differences, failed clears, unchanged/stale revisions, server-supplied required fields and partial outcomes. The generated report is authoritative for which cases executed and which desired outcomes remain unmet.

`evidence/` records the sanitized source run and runtime observations. The historical 919→928-byte HTML incident is **not** fabricated as an API fixture: the smaller synthetic closing-tag case tests the mechanism but does not claim the historical payload was replayed. Likewise, conservative refusal of an already-satisfied unchanged revision does not authorize accepting unchanged revision as success. A future fix must prove the legitimate revision/authorization/readback contract.

The frozen run (`evidence/source.json`, `evidence/verification.json`) records **23 cases, zero safety failures, five unmet desired outcomes**. Repeated fixture records were identical; `--require-fixed` exited 1 as intended. One complete Infrastructure invocation passed **2,391 tests**. The first and final complete invocations each failed one unchanged loopback-auth test: first `LoopbackUnavailable` instead of `Timeout`, finally no authorization URL. Both failures are preserved; no tests were skipped or fixed. **The final combined command exited 1: fixture exit 0, regression exit 1.** The repository suite inherits its normal environment and owns its fixture isolation; only the optional cold CLI subprocesses use empty homes and an environment allowlist. These loopback failures require independent verification before merge.

Live write qualification for HTML, identity, no-op and partial chains remains **unreproduced** without an explicitly authorized disposable ADO fixture. Existing local tests and the offline report do not satisfy that separate live-write gate. Fresh Hermes/OMP agent sessions, outer approval behavior, installation rollout and rollback remain AB#882 qualification, not simulated agent PASS results.

## AB#879 cold-read qualification

The current fixture run adds cold-refresh failure and metadata-predicate negative
controls. Cached-show counts now come from observed calls and parsed output.
Metadata readiness requires a positive diagnosis and the targeted
`twig process --refresh` recovery cue; empty or unrelated errors do not pass.
The frozen AB#878 evidence remains unchanged.

After integrating the merged AB#848 fix from `origin/main`, the September 16
AB#879 run recorded **25 cases, zero safety failures, one unmet outcome**:
legacy batch missing-ID disclosure (AB#880). `--require-fixed` therefore still
exits 1. The corrected repository pre-push gate passed **9,615 solution-wide
tests across six assemblies**, plus the external detail-host probe.

The independent-review correction exercises the real `AdoIterationService`
with an HTTP handler, not substituted adapter exceptions. Persistent 401/403,
transport failure, and process-configuration failure after successful types
must produce a single structured failure without item writes. The same
adapter's tolerant enrichment calls remain supported; their cached fallback
cannot hide a later strict recovery failure. Reverting recovery to tolerant
reads fails 15 adapter cases; strict recovery plus the cold-read suite passes
33 targeted cases, including cancellation and successful metadata responses.

`native-forwarding-probe.py --offline` exercises the installed Hermes plugin's
actual Python forwarding seam with intercepted subprocesses. For read-only live
qualification, pass `--workspace <new-empty-directory> --id <target>
--source-dll <absolute-source-built-twig.dll>`. Set `TWIG_PLUGIN_ROOT` when the
plugin is outside the default engineering profile. The live probe inherits the
caller credential home, creates only a disposable config/cache and launcher,
and calls read commands; it never installs Twig or modifies plugin configuration.
Remove the disposable workspace after retaining the JSON report.

Both modes emit explicit assertions and return nonzero on failure. The live
positive-ID check and remote missing-ID error were exercised through the actual
handler; neither mode is a fresh Hermes agent session or deployment qualification.

## Preserved provenance and prior work

Source baseline: `146387b6a9cd11a6e9dbda413b9ac57b9fcb83fb`. Installed version `0.91.6-alpha.0.8`; source build `0.91.6-alpha.0.14`. Equal-version assumptions in earlier audit notes were superseded by this direct measurement. Both engineering executable paths resolved to one installed artifact, SHA-256 `0a1a1f303c03d11b2f27c14c4b61ff0a0f4ba5fd223462ea1952b00b2bf6a4c0`; its exact source ancestry is unknown.

The actual Hermes native consumer is the Python plugin `plugins/twig/{runner,tools,schemas}.py`, **not Twig.Mcp**. The real handler forwards `--refresh` and uses a fixed configured/default workspace; the September 16 handler invocation from OMP reproduced the cache miss but was not a fresh Hermes session. OMP operational calls were shell/eval subprocesses. Runtime binding/provenance and initial output metrics are preserved in `evidence/runtime.json` without personal work-item content or credentials.

A real authorized AB#878 claim failed on the installed gate requiring server-supplied `ActivatedBy`. A **fresh** source-built proposal subsequently verified the claim at revision 2 without staging server-owned fields. This establishes deployed/source behavioral skew for AB#803. The failed original proposal was not retried. Operational claim artifacts remain private workspace evidence, not replay fixtures.

Prior ownership:

- **AB#848 / #844 / #835:** remaining HTML whitespace family; preserve pre/textarea and material-content guards.
- **AB#753 / #755:** one warning-bearing semantic comparison across apply/recovery; no new operation states.
- **AB#802 / #803:** merged identity/supplier fixes; qualify deployment rather than rebuilding them.
- **AB#634:** broader by-type skills. This workload does not redesign dispatch.
- **AB#688:** cache-reset context retention. No cache architecture changes here.
- **AB#847 / PR#439:** sandbox harness prior art. PR439 was OPEN with no merge commit when checked; its harness is not assumed to be on main, copied or merged.

Twig owns a usable no-agent fallback and complete structured review data. User-provided presenter skills and host adapters own rich terminal/agent-CLI/Discord presentation and approval controls. These observations do not justify a core presenter framework or weaker digest, authorization, revision, journal or readback gates.

Historical evidence pointers (not current synthetic responses): engineering audit `twig-agent-efficiency-20260916/audit.md`; Hermes show messages **346283/346284**, **348217/348218**, cold-create **346178/346179**, sync **346180/346181**; OMP partial-chain call **call_ezwQyx5XSRjUxcuJWWhfqEWn**, revision/repair **call_MgqbUJPEDZc2F0DMNBBDhm42**. The parent workspace retains full initial/recovery/result artifacts. Do not commit those raw records; they contain personal board content.
## AB#880 consumer-guidance acceptance

AB#880 is the source-side consumer-guidance companion to the compact-read work. Current main plus the evidence package demonstrate the intended guidance: compact projections are opt-in, the full-output route remains available, identity/freshness/completeness/errors are preserved, executable/workspace/connection binding is explicit, help reuse is version-keyed, and truncated presentation text is never parsed as JSON.

The measured **49,827 → 4,477 Unicode response characters (91.01% reduction)** applies only to the paired read-only item + requirements responses in `880-parent-evidence/paired-read-checks.json`. It excludes help/agent prompts and is **not** billed-token or global workflow savings.

Deployment remains under AB#882: install the staged engineering-profile patch, rerun the fresh Hermes/OMP qualification, and capture install/rollback proof on the live consumer paths. This README records source-side acceptance evidence; it does not claim deployed product status.
