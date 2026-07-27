---
id: 0018
title: Startup ordering — no side effects above the fast-exit
type: task
status: open
blocked_by: []
---

## Question

Graduated from [Startup cost and observability](0011-startup-and-observability.md), which found
this by measurement. Nothing to decide — the decision is made; this is the code change.

`twig --help` takes **6.5 seconds** on the first invocation after any upgrade. Two startup side
effects run unconditionally *above* the fast-exit block in `src/Twig/Program.cs`:

- `SelfUpdater.CleanupOldBinary()` — `src/Twig/Program.cs:27` (filesystem churn)
- `CompanionStartup.RunFirstRunCheck()` — `src/Twig/Program.cs:31` (**blocking GitHub release
  download, 60-second budget**, via `.GetAwaiter().GetResult()` at `:1155-1156`)

The fast-exit block sits below them at `src/Twig/Program.cs:122-156` and handles `--version`
(`:123`), `-h` / `--help` / `help` (`:128`), the no-args smart landing (`:133`), and the
unknown-command interception (`:151`). Every one of those paths pays for a network install it can
never need.

Measured on the installed 0.84.3 binary (0011, reproduced under control):

```
companions PRESENT   --help x20:  100-130 ms
companion MISSING    --help run1:    6499 ms   ("Installing companion tools...")
                            run2:     146 ms
```

Negative control: restoring the `.twig-version` marker with companions still absent returns to
119 ms, exercising the Phase-2 early return at
`src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs:46-52`.

It is once-per-version rather than every run — the marker is written at
`CompanionFirstRunCheck.cs:90-93` *after* the attempt — which is exactly why it survived: it looks
intermittent, and 0011 initially misread it as a cache-expiry boundary.

### What to do

1. Move `Program.cs:27` and `Program.cs:31` to **below** the fast-exit block at `:122-156`.
   Both must still run for real commands — this is a reordering, not a removal.
2. Note that `SQLitePCL.Batteries.Init()` (`:14`) and the UTF-8 console setup (`:17-24`) also sit
   above the block. Decide whether they move too: they are local and cheap (the `--version` path
   measures 78-99 ms against `--help`'s 100-130 ms), so the default is **leave them** unless
   moving them is free. Do not let this expand into a general startup refactor.

### Acceptance

The regression guard is **behavioural, not a wall-clock threshold** — a timing assertion in CI
would be flaky and is explicitly not wanted (0011 §2). Assert that the fast-exit paths
(`--version`, `--help`, unknown command) perform **zero network calls** — e.g. that no
`HttpClient` is constructed on those paths.

Per `AGENTS.md`, the test must **fail on the unfixed code**: confirm it fails at the pre-fix SHA in
a detached worktree before claiming it guards anything.

Budget from 0011 §2, for reference: **<150 ms and zero network** for a no-workspace command;
**<400 ms** for a local-only command.

## Answer

<!-- empty until resolved -->
