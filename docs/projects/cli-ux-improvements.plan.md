# CLI UX Improvements: --file/--stdin for Update, Non-Interactive Seed Chain

> **Status**: ✅ Done | **Epic**: #1366 | **Revision**: R3 — review feedback (tech=88, read=89)

## Executive Summary

This plan implements the two remaining CLI UX improvements under Epic #1366: (1) adding `--file` and `--stdin` flags to `twig update` so field values can be read from files or piped input instead of inline shell strings (Issue #1321), and (2) adding non-interactive batch mode to `twig seed chain` via positional title arguments for deterministic tooling use (Issue #1267). Both changes are localized to the CLI layer — the domain layer (`SeedFactory`, `FieldChange`, `IAdoWorkItemService`) and infrastructure layer (`AdoRestClient`, `SqliteCacheStore`) remain untouched. Two sibling Issues (#1334 Display titles and #1351 `twig show`) are already Done.

## Background

### Current State

The twig CLI provides work-item field updates via `twig update <field> <value>` and sequential seed creation via `twig seed chain`. Both commands have ergonomic limitations that hinder daily use and tooling integration.

**`twig update`** requires the field value as an inline positional argument. For short values (titles, state names) this is fine. For long, multi-paragraph Markdown descriptions, users must cram everything into a single shell-quoted string:

```bash
twig update System.Description "# Heading\n\nParagraph one...\n\nParagraph two..." --format markdown
```

This is error-prone (shell escaping, line breaks, quoting), impossible to review before sending, and doesn't compose with editor workflows where content lives in files.

**`twig seed chain`** uses an interactive `ReadLine()` loop that prompts for titles one at a time. While it supports piped input (prompt suppression when `Console.IsOutputRedirected`), there is no explicit CLI flag for batch creation. This makes it unusable from automated tooling (Copilot agents, CI scripts) that needs to create chains deterministically without stdin plumbing.

### Architecture Context

Both commands follow the standard twig layered architecture:

| Layer | Role | Key Files |
|-------|------|-----------|
| CLI Entry | ConsoleAppFramework `[Command]` methods in `TwigCommands` | `src/Twig/Program.cs` |
| Command | Business logic with DI-injected services | `Commands/UpdateCommand.cs`, `Commands/SeedChainCommand.cs` |
| Domain | Value objects, factories, interfaces | `SeedFactory.cs`, `FieldChange.cs`, `IConsoleInput.cs`, `ISeedLinkRepository.cs`, `IProcessConfigurationProvider.cs` |
| Hints | Post-command contextual hints | `Hints/HintEngine.cs` |
| Infrastructure | SQLite storage, ADO REST client | `SqliteCacheStore.cs`, `AdoRestClient.cs` |

Key patterns relevant to this design:
- **Constructor injection with optional defaults**: `UpdateCommand` already accepts `TextWriter? stderr = null, TextWriter? stdout = null` for testability
- **`params string[]` for rest args**: `Commit` command uses `params string[] passthrough` — ConsoleAppFramework native support
- **`IConsoleInput` abstraction**: domain-level interface for `ReadLine()` and `IsOutputRedirected`, used by `SeedChainCommand` for TTY detection

### Call-Site Audit

Both commands have exactly one production caller (`Program.cs`) and multiple test callers. Signature changes are additive — `UpdateCommand` gets `filePath`/`readStdin` params added to the `Program.cs` wrapper (12 existing test call sites unaffected; new params default to `null`/`false`). `SeedChainCommand` gets `params string[] titles` added to the `Program.cs` wrapper (13 existing test call sites unaffected; `titles` defaults to `null` → interactive mode).


## Problem Statement

1. **Long-content update friction**: `twig update` forces all content inline as shell strings. For multi-paragraph Markdown descriptions, acceptance criteria, or rich HTML content, this leads to shell escaping errors, unreadable commands, and inability to compose with file-based editor workflows.

2. **Tooling-hostile seed chain**: `twig seed chain` requires interactive stdin or pipe plumbing to create multiple seeds. Automated agents (Copilot, CI scripts) need a deterministic, single-command batch mode with explicit titles — no stdin choreography.

## Goals and Non-Goals

### Goals

| ID | Goal |
|----|------|
| G-1 | Add `--file <path>` flag to `twig update` that reads the field value from a file |
| G-2 | Add `--stdin` flag to `twig update` that reads the field value from piped stdin |
| G-3 | Enforce mutual exclusivity: exactly one of inline value, `--file`, or `--stdin` must be specified |
| G-4 | Compose with existing `--format markdown` flag (file/stdin content → Markdown → HTML → ADO) |
| G-5 | Add non-interactive batch mode to `twig seed chain` via positional title arguments |
| G-6 | Preserve full backward compatibility for all existing command invocations |
| G-7 | Unit test coverage for all new code paths |
| G-8 | AOT-compatible implementation (no reflection, TreatWarningsAsErrors clean) |

### Non-Goals

| ID | Non-Goal |
|----|----------|
| NG-1 | Editor integration — already exists via `twig edit` |
| NG-2 | Glob/wildcard file patterns for `--file` |
| NG-3 | Binary file support — `--file` reads as UTF-8 text only |
| NG-4 | Interactive multi-line editor mode for `--stdin` without piping |
| NG-5 | Undo/rollback for batch seed creation |
| NG-6 | JSON/structured input for seed chain batch mode |

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| F-1 | `twig update System.Description --file desc.md` reads the file and patches the ADO field |
| F-2 | `cat desc.md \| twig update System.Description --stdin` reads stdin and patches the ADO field |
| F-3 | `twig update System.Description --file desc.md --format markdown` reads file, converts MD→HTML, sends to ADO |
| F-4 | Providing both inline value and `--file` → exit code 2 with usage error |
| F-5 | Providing both `--file` and `--stdin` → exit code 2 with usage error |
| F-6 | Providing no value source (no inline, no `--file`, no `--stdin`) → exit code 2 with usage error ¹ |
| F-7 | `--file` with nonexistent path → exit code 2 with clear error including file path |
| F-8 | `twig seed chain --type Task "Task A" "Task B" "Task C"` creates 3 seeds with 2 successor links |
| F-9 | `twig seed chain` with no positional titles → interactive mode (backward compatible) |
| F-10 | Batch seed chain uses same parent resolution, type validation, and successor linking as interactive mode |
| F-11 | Batch seed chain outputs per-seed confirmation and summary chain arrow |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF-1 | AOT-compatible — no reflection, no dynamic type loading |
| NF-2 | TreatWarningsAsErrors — zero warnings from new code |
| NF-3 | Existing tests unaffected — all 12 UpdateCommand and 13 SeedChainCommand tests pass unchanged |
| NF-4 | Constructor injection with defaults for testability (stdin reader follows stderr/stdout pattern) |

> ¹ **Exit code convention**: twig uses exit code 1 for runtime/operational errors (network failures, item not found, concurrency conflicts) and exit code 2 for input validation/usage errors (missing arguments, invalid formats, mutual exclusivity violations). This convention is already established across 8 commands (`ConfigCommand`, `FlowCloseCommand`, `FlowStartCommand`, `NewCommand`, `SeedNewCommand`, `SetCommand`, `StateCommand`, `UpdateCommand`) with 9 `return 2` sites.

## Proposed Design

### Architecture Overview

Both changes are localized to the CLI command layer and share a common pattern: they extend an existing command's `ExecuteAsync` signature with new parameters, add a resolution step at the top of the method, and then feed the result into the unchanged downstream pipeline. No domain or infrastructure changes are required. For `UpdateCommand`, a new value-resolution phase (file → string, stdin → string, or passthrough) runs before the existing format-conversion → `FieldChange` → `PatchAsync` flow. For `SeedChainCommand`, a titles-present check gates into a batch loop that reuses the same `SeedFactory.Create` → `SaveAsync` → `AddLinkAsync` pipeline as the existing interactive loop. The CLI entry points in `Program.cs` act as thin wrappers that map ConsoleAppFramework-parsed arguments (using `params string[]` for rest-arg titles) to the command's `ExecuteAsync` method (which receives `string[]? titles`).

### Key Components

#### 1. UpdateCommand — Value Resolution

The core change adds a value-resolution step before the existing flow. The resolved string value enters the same pipeline as before (format conversion → FieldChange → PatchAsync).

**Constructor change** — add `TextReader? stdinReader` following the existing `TextWriter?` pattern:

```csharp
public sealed class UpdateCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextReader? stdinReader = null,    // NEW — injected for testability
    TextWriter? stderr = null,
    TextWriter? stdout = null)
{
    private readonly TextReader _stdin = stdinReader ?? Console.In;
    // ... existing fields ...
}
```

**ExecuteAsync signature change**:

```csharp
public async Task<int> ExecuteAsync(
    string field,
    string? value = null,             // CHANGED — was non-nullable
    string outputFormat = OutputFormatterFactory.DefaultFormat,
    string? format = null,
    string? filePath = null,          // NEW — --file flag
    bool readStdin = false,           // NEW — --stdin flag
    CancellationToken ct = default)
```

**Value resolution logic** (inserted after field validation, before conflict resolution):

```
1. Count sources: (value != null ? 1 : 0) + (filePath != null ? 1 : 0) + (readStdin ? 1 : 0)
2. If count == 0 → exit 2: "No value specified. Provide inline value, --file <path>, or --stdin."
3. If count > 1  → exit 2: "Multiple value sources. Use exactly one of: inline value, --file, or --stdin."
4. If filePath:
   a. Check File.Exists(filePath) → if not, exit 2: "File not found: {filePath}"
   b. resolvedValue = await File.ReadAllTextAsync(filePath, ct)
   c. If format is null (plain-text field): resolvedValue = resolvedValue.TrimEnd('\r', '\n')
      Rationale: File.ReadAllTextAsync preserves trailing newlines. For --format markdown this
      is harmless (HTML conversion strips them), but for plain-text fields like System.Title,
      a trailing \n would produce unexpected results. TrimEnd normalizes this.
5. If readStdin:
   a. resolvedValue = await _stdin.ReadToEndAsync(ct)
   b. If format is null: resolvedValue = resolvedValue.TrimEnd('\r', '\n')
      (Same trailing newline normalization as file path.)
   c. (Empty string is valid — consistent with `twig update field ""` for clearing a field.
      This is not an error; an explicit `--stdin` with immediate EOF produces an empty string value.)
6. If value:
   a. resolvedValue = value
7. Continue with resolvedValue through existing format conversion + patch flow
```

**Success message adjustment**: When value came from file or stdin, echo the source instead of the full content to avoid dumping entire files to the terminal:

```csharp
var displayValue = filePath is not null ? $"[from file: {filePath}]"
                 : readStdin ? "[from stdin]"
                 : value;
_stdout.WriteLine(fmt.FormatSuccess($"#{local.Id} {local.Title} updated: {field} = '{displayValue}'"));
```

#### 2. SeedChainCommand — Batch Mode

The batch mode adds an alternative code path that replaces the interactive `while (true)` loop when titles are provided explicitly.

**ExecuteAsync signature change**:

```csharp
public async Task<int> ExecuteAsync(
    int? parentOverride,
    string? type,
    string outputFormat,
    CancellationToken ct,
    string[]? titles = null)          // NEW — batch mode titles
```

**Batch mode logic** (inserted before the interactive loop):

```csharp
if (titles is not null && titles.Length > 0)
{
    // Batch mode: create seeds from explicit titles
    foreach (var title in titles)
    {
        var seedResult = SeedFactory.Create(title, parent, processConfig, typeOverride);
        if (!seedResult.IsSuccess)
        {
            // Same partial-summary-on-error pattern as interactive mode
            ...
        }
        var seed = seedResult.Value;
        await workItemRepo.SaveAsync(seed, ct);
        if (createdSeeds.Count > 0)
        {
            var previousSeed = createdSeeds[^1];
            await seedLinkRepo.AddLinkAsync(
                new SeedLink(previousSeed.Id, seed.Id, SeedLinkTypes.Successor, DateTimeOffset.UtcNow), ct);
        }
        Console.WriteLine(fmt.FormatInfo($"  #{seed.Id} {seed.Title}"));
        createdSeeds.Add(seed);
    }
}
else
{
    // Existing interactive loop (unchanged)
    ...
}
```

The seed creation, linking, and summary logic is identical between batch and interactive modes — only the title source differs.

**HintEngine behavior**: The `HintEngine.GetHints("seed-chain", ...)` call and its `Console.WriteLine` loop (lines 121–127 in the current source) execute **after** both the batch and interactive branches, unconditionally. This is correct — the batch branch must be placed inside the existing `if/else` guard **before** the hints code, not after it. Implementers must not accidentally place the batch branch after the hints block, which would skip hints for interactive mode or duplicate them for batch mode.

**Testability of batch output**: The batch mode code example uses `Console.WriteLine` directly, matching the existing interactive loop pattern throughout `SeedChainCommand`. This is an intentional deferral — injecting `TextWriter? stdout` (as `UpdateCommand` does) would require refactoring all 9 existing console output calls in the command (4 `Console.WriteLine`, 1 `Console.Write`, 4 `Console.Error.WriteLine`), which is out of scope for this Issue. Batch mode tests verify correctness through mock verification (`SaveAsync.Received()`, `AddLinkAsync.Received()`) rather than output capture. A future cleanup task could inject `TextWriter` across the entire command if output testability becomes a priority.

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| `--stdin` without piped input (TTY) | Block for EOF | Standard Unix convention — user explicitly opted in. Same as `cat`. |
| `--stdin` with empty input (immediate EOF) | Accept empty string | Consistent with `twig update field ""` which is valid for clearing a field value. `ReadToEndAsync()` returns `""`, which proceeds through the normal patch flow. No special-casing needed. |
| `--file` success message | Show file path, not content | `"updated: System.Description = '[from file: desc.md]'"`. Avoids dumping large content to terminal. |
| Value resolution in CLI layer only | Resolve file/stdin → string in `UpdateCommand` | Domain layer is value-agnostic — it just receives a string. Keeps the change localized. |
| `TextReader` injection for stdin | `TextReader? stdinReader = null` | Follows the existing `TextWriter? stderr/stdout` constructor pattern. Enables unit testing with `StringReader`. |
| `params string[]` for batch titles | Positional rest args in `Program.cs` wrapper | The `Program.cs` wrapper method uses `params string[] titles` (ConsoleAppFramework rest-arg syntax, matching the existing `Commit` command's `params string[] passthrough` pattern) to capture CLI positional arguments. The `SeedChainCommand.ExecuteAsync` method receives these as `string[]? titles = null` — a nullable array with a null default that enables the interactive fallback. The `params` keyword is a `Program.cs` concern only; the command class uses standard nullable array semantics. |
| Null/empty titles → interactive mode | `string[]? titles = null` default in `ExecuteAsync` | Empty array or null means no titles provided → fall through to existing interactive loop. Full backward compatibility. |
| ConsoleAppFramework rest-arg + named flags | `params string[]` after named params | Mixing named flags with positional rest-args (e.g., `twig seed chain --type Task "A" "B"`) is supported by ConsoleAppFramework — the `Commit` command (`params string[] passthrough`) and `Hook` command (`params string[] args`) both use this pattern with named flags before the rest-args. This must be explicitly tested (see T-1267-3) to confirm `--type` is consumed as a named flag and not captured as a title. |
| Batch output via `Console.WriteLine` | Direct console calls, not injected `TextWriter` | Deferred intentionally — `SeedChainCommand` uses direct console output throughout (9 calls: 4 `Console.WriteLine`, 1 `Console.Write`, 4 `Console.Error.WriteLine`). Refactoring to injected `TextWriter` is out of scope; tests verify via mock assertions, not output capture. |
| Trailing newline normalization | `TrimEnd('\r', '\n')` for plain-text fields only | `File.ReadAllTextAsync` and `ReadToEndAsync` preserve trailing newlines. For `--format markdown` this is harmless (HTML conversion strips whitespace), but for plain-text fields (e.g., `System.Title`), a trailing `\n` would produce unexpected results. The trim applies only when `format` is null. |

## Alternatives Considered

### Issue #1321 — Value source for `twig update`

| Alternative | Pros | Cons | Verdict |
|------------|------|------|---------|
| **Single `--from file:path\|stdin` flag** | One flag instead of two; smaller API surface | Non-standard prefix parsing; no shell tab-completion for file paths; confusing syntax (`--from file:./my file.md`); requires custom parser | ❌ Rejected — ergonomic cost outweighs surface area savings |
| **`--file` and `--stdin` as separate flags** (chosen) | Standard Unix conventions; shell tab-completion works for `--file`; clear mutual exclusivity semantics; no custom parsing | Two flags instead of one | ✅ Chosen — familiar patterns, zero parsing ambiguity |
| **`--input <path-or-dash>` flag** (dash = stdin) | Single flag; `cat foo \| twig update field --input -` is a Unix convention | Dash-means-stdin is obscure to many users; no way to distinguish "no value source" from "forgot the flag" | ❌ Rejected — dash convention is less discoverable than explicit `--stdin` |

### Issue #1267 — Batch input for `twig seed chain`

| Alternative | Pros | Cons | Verdict |
|------------|------|------|---------|
| **`--batch` flag with comma-separated titles** (`--batch "A,B,C"`) | Explicit mode switch; single string argument | Titles containing commas need escaping; non-standard delimiter convention; can't use shell word-splitting | ❌ Rejected — escaping issues defeat the ergonomic purpose |
| **JSON input** (`--batch-json '[{"title":"A"},...]'`) | Extensible (could add per-seed fields later); structured | Overkill for title-only input; shell escaping of JSON is painful; violates CLI simplicity principle | ❌ Rejected — over-engineering for the use case (NG-6) |
| **Dedicated `--titles` named flag** (`--titles "A" "B" "C"`) | Explicit intent; can't be confused with other args | ConsoleAppFramework doesn't support multi-value named flags natively; would require custom parsing or `--titles "A;B;C"` with delimiter | ❌ Rejected — framework limitation; delimiter has same escaping problem |
| **Positional `params string[]` rest-args** (chosen) | Framework-native (`Commit` and `Hook` precedents); natural shell word-splitting; no escaping for simple titles; zero custom parsing | Relies on ConsoleAppFramework correctly separating named flags from rest-args (tested explicitly in T-1267-3) | ✅ Chosen — proven pattern in codebase, most ergonomic for the common case |

## Dependencies

No new external packages required — `params string[]` is already used by `Commit` and `Hook` commands, and `File.ReadAllTextAsync` is standard library.

Issues #1321 and #1267 are fully independent — both can be implemented in any order or in parallel. Issues #1334 and #1351 are Done with no remaining dependencies.

## Impact Analysis

### Components Affected

| Component | Impact | Risk |
|-----------|--------|------|
| `UpdateCommand` | Signature change + new logic | Low — additive change, no removal |
| `SeedChainCommand` | New conditional branch | Low — existing branch untouched |
| `Program.cs` | Two method signature changes | Low — ConsoleAppFramework re-generates source |
| Test suites | New test methods | None — existing tests unaffected |

### Backward Compatibility

Both changes are fully backward-compatible:

- `twig update System.Title "New Title"` — works exactly as before (`value` still accepted as positional)
- `twig seed chain` — interactive mode unchanged (no titles → interactive loop)
- All existing flags (`--output`, `--format`, `--parent`, `--type`) retain their behavior

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ConsoleAppFramework misparses `--type Task "A" "B"` (named flag consumed as rest-arg title) | Low | Medium | Explicit integration test in T-1267-3 verifies `--type` is consumed as named flag. Precedent: `Commit` command uses same `params string[]` pattern with named flags. |
| `--file` path traversal / symlink reads unintended content | Low | Low | `File.ReadAllTextAsync` follows standard .NET file resolution. No sandboxing needed — twig runs as the user's own CLI tool with their own permissions. |
| Empty stdin hangs on TTY when user forgets to pipe | Low | Low | Documented in Design Decisions — standard Unix behavior. User explicitly opted in with `--stdin`. Ctrl+D (Unix) / Ctrl+Z (Windows) sends EOF. |
| Batch seed chain partial failure leaves orphan seeds | Low | Medium | Same partial-summary-on-error pattern as interactive mode — seeds created before the error are preserved and displayed. User can clean up with `twig seed discard <id>` for individual seeds. |

## Security Considerations

The `--file` flag introduces a file-read operation (`File.ReadAllTextAsync`) that reads arbitrary file paths specified by the user. The security analysis:

- **Path traversal**: Not a concern. Twig is a local CLI tool running with the user's own file system permissions. There is no privilege boundary — the user can already `cat` any file they can read. `File.ReadAllTextAsync` uses standard .NET path resolution (no custom parsing that could introduce injection).
- **Symlink following**: `File.ReadAllTextAsync` follows symlinks, which is standard behavior. Since twig runs as the user's process, this exposes no additional capability beyond what the user already has.
- **No network exposure**: The file content is read locally and sent to ADO via the existing authenticated `PatchAsync` flow. No file content is logged, cached locally, or sent to any third party.
- **Binary files**: `File.ReadAllTextAsync` reads as UTF-8 text. Binary files will produce garbled text but not crash or execute code. This is a non-goal per NG-3.

**Conclusion**: No security controls beyond standard path validation (`File.Exists` check with clear error message) are needed.

## Open Questions

All open questions from prior revisions have been resolved through design decisions documented in the Design Decisions table:

- **Trailing newline handling** → resolved with `TrimEnd` for plain-text fields (see Design Decisions)
- **Batch mode output testability** → deferred intentionally with mock-based verification (see Design Decisions)
- **Exit code convention** → established via codebase audit (see Requirements footnote ¹)

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| (none) | Both changes modify existing files only |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Program.cs` | `Update()`: make `value` optional (`string?`), add `file` and `stdin` params. `SeedChain()`: add `params string[] titles`. |
| `src/Twig/Commands/UpdateCommand.cs` | Add `TextReader? stdinReader` to constructor. Implement value resolution (mutual exclusivity, file read, stdin read). Update `ExecuteAsync` signature. Update success message for file/stdin sources. |
| `src/Twig/Commands/SeedChainCommand.cs` | Add `string[]? titles` parameter to `ExecuteAsync`. Add batch mode branch before interactive loop. |
| `tests/Twig.Cli.Tests/Commands/UpdateCommandTests.cs` | New tests: `--file` reads content, `--stdin` reads content, `--format markdown` composes with file/stdin, mutual exclusivity errors (multi-source, no-source), missing file error, success message variants. |
| `tests/Twig.Cli.Tests/Commands/SeedChainCommandTests.cs` | New tests: batch 3 titles, batch 1 title, empty titles fallback, batch with `--parent`/`--type` overrides, batch with invalid type. |

## ADO Work Item Structure

> **Effort legend**: S = small (<2h), M = medium (2–4h).

### Issue #1321 — twig update --file: read value from file

**Goal**: Enable reading field values from files or stdin instead of inline arguments, eliminating shell escaping friction for long content.

**Prerequisites**: None.

| Task ID | Description | Files | Effort | Implements |
|---------|-------------|-------|--------|------------|
| T-1321-1 | **CLI signature changes**: Make `value` an optional `[Argument] string?` with null default in `Program.cs`. Add `string? file = null` and `bool stdin = false` named parameters. Update `UpdateCommand.ExecuteAsync` signature to match. | `Program.cs`, `UpdateCommand.cs` | S | F-1, F-2, F-6 |
| T-1321-2 | **Value resolution + stdin injection**: Add `TextReader? stdinReader = null` to `UpdateCommand` constructor (following stderr/stdout pattern). Implement mutual exclusivity validation (count sources, error if != 1). Implement `File.ReadAllTextAsync` with existence check and clear error for missing files. Apply `TrimEnd('\r', '\n')` to file/stdin content when `format` is null (plain-text fields only — see Design Decisions). Implement `_stdin.ReadToEndAsync()` for stdin path. Display `[from file: <path>]` or `[from stdin]` in the success message instead of echoing potentially large content. | `UpdateCommand.cs` | M | F-1, F-2, F-3, F-4, F-5, F-7 |
| T-1321-3 | **Unit tests**: File reads content and patches correctly. Stdin reads content and patches correctly. `--format markdown` composes with both file and stdin. Multi-source (file + stdin, value + file) returns exit 2. No source returns exit 2. Missing file returns exit 2 with path. Empty stdin (immediate EOF) produces empty-string patch. Success message shows file path / stdin indicator. File with trailing newline: plain-text field trims trailing `\n`, `--format markdown` preserves it. | `UpdateCommandTests.cs` | M | G-7 |

**Acceptance Criteria**:
- [x] `twig update System.Description --file desc.md` reads file and patches ADO
- [x] `cat desc.md | twig update System.Description --stdin` reads stdin and patches ADO
- [x] `--file desc.md --format markdown` converts file contents from Markdown to HTML
- [x] `--stdin --format markdown` converts stdin content from Markdown to HTML
- [x] Providing inline value + `--file` → exit 2 with clear error
- [x] Providing `--file` + `--stdin` → exit 2 with clear error
- [x] No value source at all → exit 2 with clear error
- [x] `--file` with nonexistent path → exit 2 with file path in error message
- [x] Success message shows `[from file: <path>]` or `[from stdin]` instead of raw content
- [x] `--stdin` with empty input (immediate EOF) patches an empty string (consistent with `twig update field ""`)
- [x] `--file` with trailing newline in plain-text field → `TrimEnd` removes trailing `\r\n`
- [x] `--file` with trailing newline in `--format markdown` → trailing newline preserved (HTML conversion handles it)
- [x] All 12 existing UpdateCommand tests pass unchanged
- [x] New tests cover all file, stdin, and validation paths

### Issue #1267 — Add non-interactive seed chain creation for tooling

**Goal**: Enable deterministic batch seed chain creation from CLI arguments without interactive prompts, unblocking automated tooling workflows.

**Prerequisites**: None.

| Task ID | Description | Files | Effort | Implements |
|---------|-------------|-------|--------|------------|
| T-1267-1 | **CLI signature + batch loop**: Add `params string[] titles` to `TwigCommands.SeedChain()` in `Program.cs`. Add `string[]? titles = null` parameter to `SeedChainCommand.ExecuteAsync`. Add `if (titles is not null && titles.Length > 0)` guard routing to batch vs. interactive mode. In the batch branch: iterate titles, create seeds via `SeedFactory.Create`, persist via `workItemRepo.SaveAsync`, link to previous seed via `seedLinkRepo.AddLinkAsync`. Output per-seed confirmation via `Console.WriteLine(fmt.FormatInfo(...))`. On `SeedFactory.Create` failure, print seeds created so far before the error message, then return exit code 1 (same partial-summary-on-error pattern as interactive mode). After successful batch, print the chain summary (`"Created N seeds: #-X → #-Y → #-Z"`). Place the batch branch **before** the HintEngine block (lines 121–127) so hints display unconditionally after both branches. | `Program.cs`, `SeedChainCommand.cs` | M | F-8, F-9, F-10, F-11 |
| T-1267-2 | **Unit tests**: Batch 3 titles → 3 seeds + 2 successor links. Batch 1 title → 1 seed + 0 links. Empty/null titles → interactive mode (verify `ReadLine` called). Batch with `--parent` override. Batch with `--type` override. Batch with invalid child type → error. ConsoleAppFramework rest-arg parsing: verify `--type Task "A" "B"` correctly separates `--type` as named flag from `"A" "B"` as rest-arg titles (not captured as titles). | `SeedChainCommandTests.cs` | M | G-7, NF-3 |

**Acceptance Criteria**:
- [x] `twig seed chain --type Task "A" "B" "C"` creates 3 seeds with 2 successor links
- [x] `twig seed chain "Solo"` creates 1 seed with no links
- [x] `twig seed chain` with no titles → interactive mode (backward compatible)
- [x] `--parent` and `--type` overrides work correctly in batch mode
- [x] Invalid child type in batch mode → exit 1 with error
- [x] Mid-batch error prints partial summary before error message
- [x] Summary output shows chain: `"Created 3 seeds: #-X → #-Y → #-Z"`
- [x] All 13 existing SeedChainCommand tests pass unchanged
- [x] New tests cover batch happy paths, error paths, and interactive fallback
- [x] `--type Task "A" "B"` correctly parses `--type` as named flag (not captured as title)

## PR Groups

PR groups cluster tasks for reviewable pull requests. Both Issues are independent with no cross-dependencies, enabling parallel execution.

> **PR classification tiers**: **Deep** = few files (≤5), complex logic changes requiring careful review. **Wide** = many files (>5), mechanical/repetitive changes. Both PRs below are Deep.

### PR-1: `twig update --file/--stdin` (Issue #1321)

| Property | Value |
|----------|-------|
| **Tasks** | T-1321-1, T-1321-2, T-1321-3 |
| **Files** | `Program.cs`, `UpdateCommand.cs`, `UpdateCommandTests.cs` |
| **Classification** | Deep — 3 files, complex value resolution logic |
| **Estimated LoC** | ~250 (80 production + 170 test) |
| **Predecessors** | None |

**Rationale**: All three tasks modify closely coupled code (constructor, signature, resolution logic) in a single command. Splitting would create incomplete intermediate states. Tests must accompany the implementation to validate mutual exclusivity rules.

### PR-2: `twig seed chain` batch mode (Issue #1267)

| Property | Value |
|----------|-------|
| **Tasks** | T-1267-1, T-1267-2 |
| **Files** | `Program.cs`, `SeedChainCommand.cs`, `SeedChainCommandTests.cs` |
| **Classification** | Deep — 3 files, batch loop + error handling logic |
| **Estimated LoC** | ~220 (65 production + 155 test) |
| **Predecessors** | None |

**Rationale**: Both tasks modify closely coupled code in a single command. Shipping them as one PR ensures the feature is complete and reviewable. The `Program.cs` change (adding `params string[]`) modifies a different method than PR-1, so no merge conflict risk.

Both PRs are independent and can be developed, reviewed, and merged in parallel. Neither PR exceeds the ≤2000 LoC / ≤50 files guardrails.

## Completion

> **Completed**: 2026-04-14

All planned work is complete. Both PRs have been merged to main:

- **PR #21** — `twig update --file/--stdin` (Issue #1321): Merged. Adds `--file` and `--stdin` flags with mutual exclusivity validation, stdin injection for testability, and trailing newline handling.
- **PR #22** — `twig seed chain` batch mode (Issue #1267): Merged. Adds `params string[]` rest-arg support for deterministic batch seed creation, with chain summary output.

All acceptance criteria verified. All existing tests pass unchanged. No regressions.

