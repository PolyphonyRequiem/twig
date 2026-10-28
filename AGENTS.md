# Repository guidance

## Build & verification

For routine implementation and draft PRs, build the supported solution, run a
focused regression for the changed behavior, then run the offline pre-push smoke:

```bash
python tools/run-bounded.py --timeout 120 -- dotnet build "-m:1"
python tools/run-bounded.py --timeout 30 -- bash tools/run-tests.sh --pre-push
```

`global.json` selects the SDK; use the system `dotnet`. On this Windows workstation,
invoke `C:\Program Files\Git\bin\bash.exe` for the script, not WSL's `bash`:
the Windows-built binary and Windows Python/.NET must run in the same environment.
An unavailable interpreter is a failed invocation, not permission to install or
repair machine-wide tools.

The smoke validates an already-built CLI offline with a hard **15-second** deadline.
It does not build, start test hosts, publish work items, or perform work HTTP.
The current script and `.github/workflows/ci.yml` define the checks actually run;
`README.md` describes the routine versus release-candidate validation split.
Complete platform suites are a **release-candidate** gate, not a routine pre-push
or draft-PR requirement. Explicit suite arguments are for deliberately requested
broad validation, not an automatic response to a focused fix or smoke failure.

### Tight feedback and deadlines

- Prove a bug regression red on unfixed production composition and green after
  the fix. Mock only external boundaries; keep the real failing path in the loop.
  Run focused commands through `python tools/run-bounded.py --timeout 60 -- <command>`.
- Run builds and test commands serially; concurrent invocations collide over
  shared output. Use a **120-second** external deadline for routine builds and
  **60 seconds** for focused regression commands, including child processes.
- On a deadline or failure, terminate and reap that command's owned process tree,
  preserve output and the native exit code, and report the failed check. Diagnose
  with a smaller deterministic reproducer; a longer deadline is not a correction.
  Keep the repository's existing test watchdog unchanged. Extended runsettings
  and whole-suite retries require a separate, explicit user request.
- Capture each native child exit before any pipeline or later command replaces
  it. A `Passed!` summary from an aborted host is not a pass. Report the reconciled
  `TWIG-VERDICT` and every failed child; an optional broad failure remains visible
  even when routine gates pass.
- Launch supervised coding workers through `tools/run-bounded.py` with a finite
  task-local deadline (at most 600 seconds), independent of their model instructions
  and tool wait windows. When it expires, its owned tree is stopped and exit 124
  preserves the failure. Keep the saved session for bounded recovery; resume that
  session rather than starting a duplicate writer.

Completion means the changed behavior and current required gates have actually
passed, or the exact blocker is reported. No timeout expansion, narrowed claim,
environment repair, or green-looking partial summary substitutes for that proof.


## Testing

- A regression test must fail on the unfixed code; one that passes before and after
  proves nothing. Check in a detached worktree at the pre-fix SHA.
- Assert fixture preconditions so a setup change cannot silently route a test onto
  the happy path.
- Prefer a few high-value tests at real boundaries over many shallow ones.

## Git

- The repo squash-merges: branch SHAs are not ancestors of `main` after merge. Verify
  content landed with `git show origin/main:<path>` instead.
- GitHub close keywords do not chain: write `Fixes #1, fixes #2`.

## Where to find context

| Need | Look in |
|---|---|
| Work items, status, scheduling | ADO board `PolyphonyRequiem/Twig` via the `twig` CLI (`docs/agents/issue-tracker.md`). GitHub is the public record only. |
| Triage tags | `docs/agents/triage-labels.md` |
| Domain language and decisions | `CONTEXT.md`, `docs/adr/`, `wayfinder/`, `wayfinder-1.0/`, `docs/specs/` (`docs/agents/domain.md`) |
| Ruling ↔ work-item links | `tools/check-tracking.sh` (`--selftest` after editing it) |
| What CI and the smoke actually run | `.github/workflows/ci.yml`, `tools/run-tests.sh`, `README.md` |
| Past test-host stalls and broad-suite gaps | `docs/incidents/broad-suite-and-test-host-stalls.md` |
| False-green defects and mutation harnesses | `docs/incidents/false-green-class.md` |

Read incident docs only when diagnosing a matching symptom.
