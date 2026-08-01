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

<!-- empty until resolved -->
