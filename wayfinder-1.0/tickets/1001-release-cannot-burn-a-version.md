---
id: 1001
title: Release pipeline cannot burn a version number
type: task
status: open
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

**Status: fix applied, acceptance NOT yet met.** The demonstration this ticket
requires has not been performed — see "Outstanding" below. `status:` stays `open`
deliberately; do not flip it on the strength of the code change alone.

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

### Outstanding — the acceptance gate

> A failing build leg leaves NuGet untouched — **demonstrated, not asserted.**

Not done. Reading the corrected YAML is precisely the evidence this ticket refuses,
since the pre-fix graph also looked fine to a reader. Three ways to close it, in the
order recommended:

1. **Throwaway tag against the real workflow with the push step temporarily
   swapped for an `echo`.** Real pipeline, real deliberately-failed leg, real skip
   observed; zero credential exposure. Costs a burned throwaway version string and
   some Actions minutes. Weaker than (2) by exactly one link — it does not prove the
   push step itself is wired to real secrets correctly, which the last several
   successful releases already evidence.
2. **Throwaway tag against the untouched live workflow.** Strongest proof, but runs
   against live `NUGET_API_KEY` and the `release` environment's Azure OIDC
   federation, with the safety of the run resting entirely on the fix being proved.
3. **Scratch simulation workflow** with the same dependency shape and stubbed
   publish steps. Safe and fast, but proves only that `needs:` skips a downstream
   job when an upstream matrix leg fails — a fact about GitHub Actions, not about
   twig's pipeline. If this route is taken, the evidence level must be recorded as
   graph-level, not pipeline-level.

This is a human gate: every option pushes a tag to the real repository. The session
that applied the fix asked for the call and did not receive one, and declined to
choose on the owner's behalf.

### Note on #357

Referenced, not closed. Its release-pipeline half is this ticket; its
cross-platform-CI-matrix half is out of scope for this map by decision. Closing it
wholesale would silently retire that deferred decision.
