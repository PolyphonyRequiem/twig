# Repository guidance

## Build & test

`global.json` pins SDK **11.0.100-preview.3.26207.106** with `rollForward: disable`.
That SDK is **not** installed system-wide on every dev box, so `dotnet` on the bare
PATH will fail with:

```
Requested SDK version: 11.0.100-preview.3.26207.106 ... not found
```

Newer previews genuinely cannot build this repo (`error CS0433: The type 'IUnion'
exists in both 'Twig.Domain' and 'System.Runtime'` — the discriminated-union types
moved into `System.Runtime` after preview.3). **Do not "fix" this by editing
`global.json`.** It is a real incompatibility, not a stale pin.

Export the SDK location first, then build/test. These exports must be repeated in
each new shell:

```bash
export DOTNET_ROOT="$HOME/.dotnet-p3"
export PATH="$HOME/.dotnet-p3:$PATH"
export DOTNET_MULTILEVEL_LOOKUP=0
```

### Canonical test command

`dotnet test` accepts only **one** project per invocation, and two concurrent runs
collide over shared build output (producing a bogus `SQLitePCL DllNotFoundException`).
Run them **serially**:

```bash
dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo --filter "FullyQualifiedName!~BinaryLauncher"
dotnet test tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj --nologo
dotnet test tests/Twig.Mcp.Tests/Twig.Mcp.Tests.csproj --nologo
dotnet test tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj --nologo
```

`BinaryLauncherTests` is excluded because it spawns a child binary that cannot
resolve the SQLite native lib under a user-local SDK, killing the test host
mid-run. It is environmental, not a repo defect, and passes in CI.

Do **not** build `Twig.slnx` — `tests/Twig.Benchmarks` fails with a pre-existing
`CS0433 ILoggingBuilder` ambiguity regardless of your changes. Build
`src/Twig/Twig.csproj` and the test projects directly.

### Reading test results

**Trust the process exit code, not the summary line.** An aborted run still prints
a clean-looking `Passed! - Failed: 0` with a smaller total, and a TRX report's
counters only describe the portion completed before the host died. Always capture
the exit code, and include `Aborted` in any grep:

```bash
dotnet test ... > log 2>&1; echo "EXIT=$?"
grep -E "Passed!|Failed!|Aborted|\[FAIL\]" log
```

Reporting "suite green" from a summary grep while the process exits non-zero has
already cost one bogus issue report (#257, closed as invalid).

## Testing conventions

Regression tests must **fail on the unfixed code**. A test that passes both before
and after proves nothing. To check, add the fix's tests to a detached worktree at
the pre-fix SHA and confirm they fail there:

```bash
MSYS_NO_PATHCONV=1 git worktree add --detach ../twig-baseline <pre-fix-sha>
```

Watch for fixtures that silently degrade into the happy path — e.g.
`ConflictResolver.Resolve` short-circuits to `NoConflict` when local and remote
revisions match, so a conflict-path test must advance the remote revision
(`remote.MarkSynced(n)`) or the branch under test never runs. Where a fixture has
such a precondition, assert it explicitly so a future setup regression can't
hollow the suite out.

`MergeResult` is a `union`: pattern-match the case (`result is HasConflicts`).
`ShouldBeOfType<HasConflicts>()` fails against the wrapper type.

## Git

The repo **squash-merges**, so branch SHAs are not ancestors of `main` after merge —
`git merge-base --is-ancestor` returning false is expected, not a failed merge.
Verify content landed instead:

```bash
git show origin/main:path/to/File.cs | grep -c "YourNewSymbol"
```

GitHub auto-close keywords do not chain: `Fixes #253 and #252` closes only #253.
Repeat the keyword — `Fixes #253, fixes #252`.
