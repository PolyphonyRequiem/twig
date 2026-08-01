---
id: 1001
title: Release pipeline cannot burn a version number
type: task
status: resolved
blocked_by: []
---

## Question

Make the NuGet publish depend on the platform builds succeeding, so a partial release
cannot permanently consume a version number.

**Verified defect** (`.github/workflows/release.yml:249`): the `nuget` job declares
`needs: verify-ci`, not `needs: build`. NuGet packages therefore publish in parallel with
the three platform build legs rather than after them.

**Observed consequence:** v0.85.0 pushed its packages, then two build legs failed. No
binaries exist under that version, and NuGet versions cannot be re-pushed — so v0.85.0 is
permanently burned and v0.85.1 had to be cut. Shipping 1.0 through this pipeline is
shipping a known mechanism for destroying the string `1.0.0` with no recovery path.

## Scope

The one-line dependency change, plus whatever proves it works.

**Explicitly NOT in scope:** the other half of #357 — Linux-only CI hiding Windows/macOS
breakage until release day. That is out of scope for this map by decision; do not expand
into it here.

## Acceptance

- The `nuget` job does not run unless every platform build leg succeeded.
- A failing build leg leaves NuGet untouched — demonstrated, not asserted. A dry-run or a
  deliberately-failed leg on a throwaway tag is worth more than reading the YAML.
- Confirm no other job has the same shape. `release` already declares `needs: build`;
  check nothing else publishes anything irreversible off `verify-ci` alone.

## Answer

**Status: RESOLVED — fix applied and demonstrated by execution against the real
pipeline with live credentials (option A, the strongest available evidence).**

### What changed

`.github/workflows/release.yml`: the `nuget` job's `needs: verify-ci` became
`needs: build`, with a comment recording *why* so a future edit cannot quietly
revert it. Resulting graph, dumped from the parsed YAML rather than read by eye:

```
verify-ci <- needs: None
build     <- needs: verify-ci
release   <- needs: build
nuget     <- needs: build
```

### Audit of every other job (Acceptance bullet 3)

The repo has three workflow files. `ci.yml` (one job, `build-and-test`) and
`benchmarks.yml` (one job, `benchmarks`) contain no publish step and reference no
secrets — grepped for `publish`, `push`, `release`, `secrets.`; nothing matched
beyond the trigger keyword `push:`. Only `release.yml` publishes anything, and its
two publishing jobs are `release` (GitHub Release — reversible, and already
`needs: build`) and `nuget` (irreversible, now `needs: build`). **Nothing publishes
anything irreversible off `verify-ci` alone.**

### `fail-fast` posture — deliberate, kept as `false`

Stated as a choice rather than left as an accident, and written into the workflow
as a comment. With `nuget` downstream of `build`, a failing leg no longer risks
anything irreversible; it only lets its siblings run to completion. That is worth
paying for, because it surfaces all three platforms' failures from one tag push
instead of one per re-cut. If this ever changes, the reason to flip it would be CI
minutes, not safety.

### `--skip-duplicate` is not mitigation

Recording this so it is not misread later. `--skip-duplicate` on the push makes
*re-runs* of an identical version safe. The failure mode here was publishing a
version that should never have existed at all, which it does nothing about. Its
presence on line ~281 was never protection against this defect.

### The demonstration (Acceptance bullet 2) — DONE, by execution

> A failing build leg leaves NuGet untouched — **demonstrated, not asserted.**

Owner authorised the strongest option: a throwaway tag against the **untouched live
workflow**, with real `secrets.NUGET_API_KEY` and the real `release` environment.
Run **30706852988**, tag `v0.0.0-test1001`, 2026-08-01.

Job outcomes, read from the Actions API rather than the web UI:

```
Verify CI green on tagged commit          completed   success
build (win-x64, windows-latest, zip)      completed   failure   <- deliberate
build (osx-arm64, macos-latest, tar.gz)   completed   success
build (linux-x64, ubuntu-latest, tar.gz)  completed   success
Publish NuGet packages                    completed   SKIPPED   <- the proof
release                                   completed   skipped
```

Pre-fix, `nuget` would have run to completion here: `verify-ci` was green, which was
its only gate. It was skipped instead, with live credentials present and a working
push step — so the skip is attributable to the dependency change and nothing else.

**Confirmed against nuget.org itself, not merely inferred from the skip.** All three
package feeds queried directly (HTTP 200, feeds exist and are current at `0.85.1`);
none contains `0.0.0-test1001`. Nothing was published.

#### Three design choices in the test that a reader should not have to reverse-engineer

1. **Version safety was verified before the tag was pushed, not assumed.** Versioning
   is MinVer with `MinVerTagPrefix=v`, so the package version derives from the tag —
   a `v0.0.0-test1001` tag can only produce a `0.0.0-test1001` package. Had versions
   been hardcoded in the csprojs, this test could itself have burned a real version,
   i.e. caused the exact harm it was proving prevented. Check this again before any
   repeat.
2. **The deliberate failure had to be one CI structurally cannot see.** `verify-ci`
   refuses to proceed unless CI is green on the tagged commit, so any ordinary
   breakage would have halted at that gate and proved nothing about the build→nuget
   edge. A `win-x64`-only failure passes the gate because CI is Linux-only — the
   other half of #357, used here as the test instrument rather than fixed.
3. **The tag was cut on a PR branch head, not on `main`.** `verify-ci` needs a CI run
   on the tagged SHA, and CI triggers only on `main` pushes and pull requests. A
   draft PR (#361) produced a green run on the exact branch-head SHA — verified first
   that PR-triggered runs record the branch head rather than a synthetic merge commit.
   This avoided pushing a knowingly-broken commit to `main`.

#### Cleanup, verified

Test tag deleted locally and on the remote; draft PR #361 closed and its branch
deleted; `gh release list` confirms no GitHub Release was created (latest remains
`v0.85.1`). The temporary failure step was removed — the working tree carries the
dependency fix and comments only, and `grep -c DELIBERATE` returns 0.

⚠️ **`v0.0.0-test1001` should not be reused as a tag name.** Deleting a tag does not
un-publish anything, and although nothing reached nuget.org this time, reusing the
string invites confusion with this run's Actions history.

### Note on #357

Referenced, not closed. Its release-pipeline half is this ticket; its
cross-platform-CI-matrix half is out of scope for this map by decision. Closing it
wholesale would silently retire that deferred decision.
