# Repository guidance

## Build & test

`global.json` pins SDK **11.0.100-preview.5.26302.115** with `rollForward: latestFeature`.

That SDK **is** installed system-wide here (`C:\Program Files\dotnet`), so plain `dotnet`
on the PATH works. No exports are needed for a normal build:

```bash
dotnet build src/Twig/Twig.csproj -m:1
```

### History: the old preview.3 pin (#333)

The repo used to pin **11.0.100-preview.3.26207.106** with `rollForward: disable`, because
`src/Twig.Domain/Common/CompilerPolyfill.cs` declared `UnionAttribute` / `IUnion` into
`System.Runtime.CompilerServices` — and from preview.5 the runtime ships those types itself,
producing `error CS0433: The type 'IUnion' exists in both 'Twig.Domain' and 'System.Runtime'`.

That is now resolved by scoping the shim to `net10.0` only (`Twig.Domain.csproj` removes it
from the compile for every other TFM), so the pin no longer needs to hold newer SDKs back.
The shim still exists and is still required for the `net10.0` target, whose ref pack does not
carry the types — do not delete it until `net10.0` is dropped.

**If you have a stale `DOTNET_ROOT` exported** (e.g. `$HOME/.dotnet-p3` from the old
instructions), test hosts fail with *"You must install or update .NET to run this
application"* listing only preview.3. `unset DOTNET_ROOT`.

### Canonical test command

**Use `tools/run-tests.sh`.** It runs the four suites serially with the right
filters and prints a single reconciled verdict per suite:

```bash
tools/run-tests.sh              # all four
tools/run-tests.sh Cli Domain   # a subset
```

It exits non-zero unless every suite is a genuine, unaborted pass. Grep its
output for `TWIG-VERDICT` — never for `Passed!` (see "Reading test results").

The underlying commands, if you need to run one by hand. `dotnet test` accepts
only **one** project per invocation, and two concurrent runs collide over shared
build output (producing a bogus `SQLitePCL DllNotFoundException`). Run them
**serially**:

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
counters only describe the portion completed before the host died.

`tools/run-tests.sh` exists precisely so this is not a judgement call. It
reconciles the exit code, the abort markers, and the test total, and emits one
verdict line that cannot grep as a pass unless the run really passed:

```bash
tools/run-tests.sh Cli | grep TWIG-VERDICT
# TWIG-VERDICT Cli: PASSED (2941 tests) [log: artifacts/test-logs/Cli.log]
# TWIG-VERDICT OVERALL: PASSED
```

If you must invoke `dotnet test` directly, capture the exit code and include
`Aborted` in the grep — `grep -E "Passed!|Failed!"` alone matches the false-green
summary line an aborted run prints:

```bash
dotnet test ... > log 2>&1; echo "EXIT=$?"
grep -E "Passed!|Failed!|Aborted|\[FAIL\]" log
```

Reporting "suite green" from a summary grep while the process exits non-zero has
already cost one bogus issue report (#257, closed as invalid), and the underlying
hang that produced those aborted runs was #311.

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
