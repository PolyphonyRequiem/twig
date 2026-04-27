---
name: twig-command-dev
description: Standards and completeness checklist for developing, reviewing, and auditing twig CLI commands. Load when creating new commands, modifying existing commands, reviewing PRs that touch command code, or auditing command quality. Covers output formatting, help text, error handling, telemetry, testing, and MCP parity.
---

# Twig Command Development & Completeness Standards

This skill defines what a **correct and complete** twig CLI command looks like.
Use it when creating, modifying, reviewing, or auditing commands.

## Universal Standards

Every twig CLI command MUST satisfy these requirements. No exceptions unless
explicitly noted (e.g., pure-utility commands like `upgrade`).

### 1. Output Format Support

**Rule:** All non-interactive commands MUST support both human and machine-readable output.

```
--output human       # Default. Rich ANSI, Spectre tables, icons.
--output json        # Full structured JSON. Machine-consumable.
--output jsonc       # Compact JSON (json-compact). Reduced schema.
--output minimal     # Pipe-friendly. Key fields, tab-separated, one per line.
--output ids         # Bare numeric IDs, one per line. For piping to other commands.
```

**`ids` format:** Supported by all list-like commands: `query`, `workspace`,
`workspace area view`, `show` (batch/tree). NOT supported by single-item detail
views, `process`, or `sync`.

**Implementation pattern:**
```csharp
public async Task<int> ExecuteAsync(
    string outputFormat = OutputFormatterFactory.DefaultFormat, ...)
{
    var fmt = formatterFactory.GetFormatter(outputFormat);
    // ... use fmt.FormatXxx() for all output
}
```

**Machine format behavior (json, jsonc, minimal):**
- MUST NOT use the render/async-update/re-render pattern
- MUST NOT use Spectre Live regions or async rendering
- MUST sync first, then produce output in a single write
- Output MUST be parseable without streaming — one complete JSON object/array

**Human format behavior:**
- MAY use Spectre Live rendering with async background refresh
- MAY render cached data first, then re-render after sync completes
- MUST clearly indicate stale data (e.g., "cached 5m ago")

**Exceptions:** Commands that are inherently interactive (e.g., `edit` with an
editor, conflict resolution prompts) don't need JSON output for the interactive
flow itself, but MUST emit a JSON result on completion.

### 2. Help Text

**Rule:** Every command MUST have thorough `--help` output.

Required elements:
- **One-line summary** — What the command does (in the `<summary>` XML doc)
- **Parameter documentation** — Every parameter has a `<param>` doc with:
  - Human-readable description
  - Type and default value
  - Valid values (for enums/choices)
  - Short alias if applicable (e.g., `-o` for `--output`)
- **Usage examples** — At minimum 2 examples:
  - Basic/common usage
  - Advanced usage with flags
- **Examples are registered** in `CommandExamples.cs`

**Pattern (in Program.cs):**
```csharp
/// <summary>Show the active work item with full detail.</summary>
/// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
/// <param name="noRefresh">Skip background sync after display.</param>
/// <param name="depth">Maximum tree depth for child display.</param>
[Command("show")]
public async Task<int> Show(
    string output = OutputFormatterFactory.DefaultFormat,
    bool noRefresh = false,
    int depth = 1,
    CancellationToken ct = default) => ...
```

**In CommandExamples.cs:**
```csharp
["show"] = [
    "twig show              Show the active work item",
    "twig show -o json      Show as JSON for scripting",
    "twig show --depth 3    Show with 3 levels of children",
],
```

### 3. Error Handling

**Rule:** Consistent exit codes and format-aware error output.

| Exit Code | Meaning | When |
|-----------|---------|------|
| `0` | Success | Command completed normally |
| `1` | Error | Runtime failure (ADO unreachable, conflict, etc.) |
| `2` | Usage error | Invalid arguments, missing required input |

**Error output pattern:**
```csharp
// Errors go to stderr, formatted for the active output format
Console.Error.WriteLine(fmt.FormatError("Work item #42 not found."));
return 1;

// Usage errors
Console.Error.WriteLine(fmt.FormatError("Missing required argument: <id>"));
return 2;
```

**Rules:**
- NEVER write raw strings to stderr — always use `fmt.FormatError()`
- NEVER swallow exceptions silently — log or report them
- Errors in JSON format MUST produce parseable JSON on stderr:
  `{"error": "Work item #42 not found."}`
- Use `Result<T>` types for domain operations that can fail

### 4. Telemetry

**Rule:** Every command MUST be instrumented with telemetry.

**Required properties (safe to send):**
- `command` — command name (e.g., "status", "tree")
- `exit_code` — 0, 1, or 2
- `output_format` — "human", "json", etc.
- `twig_version` — from `VersionHelper.GetVersion()`
- `os_platform` — from `RuntimeInformation.OSDescription`

**Optional properties (when relevant):**
- `duration_ms` — command execution time
- Generic counts (e.g., `result_count`, `item_count`)
- Generic booleans (e.g., `had_profile`, `merge_needed`)

**NEVER send** (per telemetry.instructions.md):
- Organization, project, team, user names
- Work item IDs, titles, types, field names
- Area paths, iteration paths, process templates

**Pattern:**
```csharp
public async Task<int> ExecuteAsync(...)
{
    var startTimestamp = Stopwatch.GetTimestamp();
    var exitCode = await ExecuteCoreAsync(...);
    telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
    {
        ["command"] = "commandname",
        ["exit_code"] = exitCode.ToString(),
        ["output_format"] = outputFormat,
        ["twig_version"] = VersionHelper.GetVersion(),
        ["os_platform"] = RuntimeInformation.OSDescription
    }, new Dictionary<string, double>
    {
        ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
    });
    return exitCode;
}
```

### 5. Registration

**Rule:** Commands are registered via ConsoleAppFramework in `Program.cs`.

- Command class in `src/Twig/Commands/`
- Registration in `Program.cs` with `[Command("name")]` attribute
- XML doc comments on the registration method (these become help text)
- Examples in `CommandExamples.cs`
- Service registration in `TwigServiceRegistration.cs` if the command has
  non-trivial DI dependencies

### 6. JSON Serialization (AOT Safety)

**Rule:** All types that appear in JSON output MUST be registered in `TwigJsonContext`.

Because `PublishAot=true` and `JsonSerializerIsReflectionEnabledByDefault=false`,
reflection-based serialization is not available. Every type used in JSON output
must have a `[JsonSerializable(typeof(T))]` attribute on `TwigJsonContext`.

For commands using manual `Utf8JsonWriter` (most formatter output), this is less
critical — but any use of `JsonSerializer.Serialize<T>()` requires registration.

### 7. Testing

**Rule:** Every command MUST have dedicated test coverage.

**Test location:** `tests/Twig.Cli.Tests/Commands/<CommandName>Tests.cs`

**Required test scenarios:**
- Happy path (basic execution, expected output)
- Error paths (invalid input, missing items, ADO failures)
- All output formats (at least json + human)
- Edge cases specific to the command

**Test conventions:**
- xUnit with `[Fact]` and `[Theory]`
- Shouldly assertions
- NSubstitute for mocking
- Test class name matches `<CommandName>Tests`

### 8. MCP Parity

**Rule:** Commands that are useful to AI agents SHOULD have MCP tool equivalents.

Not every command needs an MCP tool (e.g., `upgrade`, `web`, `hooks` are
human-only). But commands that agents use for reading, mutating, or navigating
work items should have corresponding tools in `src/Twig.Mcp/`.

**Priority for MCP parity:**
- Context commands (set, show) — ✅ have MCP tools
- Mutation commands (state, update, note, patch, sync) — ✅ have MCP tools
- Seed commands (new, publish, chain, reconcile) — ❌ missing
- Query — ❌ missing (add `twig_query` MCP tool)
- Process — ❌ missing (add `twig_process` MCP tool)
- Workspace area — no MCP (CLI config concern)
- Flow/git commands — ❌ removed (not applicable)

### 9. --no-refresh Pattern

**Rule:** Read commands that normally trigger a background sync MUST support
`--no-refresh` to skip it.

This applies to: `show`, `status`, `tree`, `workspace`

Machine output formats (json, jsonc, minimal) should sync-then-output
synchronously — no background refresh needed. `--no-refresh` is primarily
for human format when the user wants instant cached results.

---

## Command Audit Checklist

When auditing or reviewing a command, verify each item:

```
□ --output support (human, json, jsonc, minimal)
□ Machine formats sync-then-output (no async rendering)
□ Help text: summary, all param docs, 2+ examples
□ Examples registered in CommandExamples.cs
□ Exit codes: 0 (success), 1 (error), 2 (usage)
□ Errors use fmt.FormatError() — never raw strings
□ Telemetry instrumented (command, exit_code, format, version, os)
□ Registered in Program.cs with XML doc comments
□ Services registered in TwigServiceRegistration.cs
□ JSON types registered in TwigJsonContext (if using JsonSerializer)
□ Dedicated test file in Twig.Cli.Tests/Commands/
□ Tests cover: happy path, errors, json output, edge cases
□ MCP tool exists (if applicable to agent workflows)
□ --no-refresh supported (if read command with sync)
```

---

## Known Gaps (as of v0.57.0)

Identified via audit — these are NOT blocking but should be tracked:

### Missing Telemetry (37 commands)
AreaCommand, ArtifactLinkCommand, BatchCommand, BranchCommand, ChangelogCommand,
CommitCommand, ConfigCommand, EditCommand, FlowCloseCommand, FlowDoneCommand,
FlowStartCommand, GitContextCommand, HooksCommand, LinkCommand, LinkBranchCommand,
LogCommand, NewCommand, NoteCommand, PrCommand, SaveCommand, SeedChainCommand,
SeedDiscardCommand, SeedEditCommand, SeedLinkCommand, SeedNewCommand,
SeedPublishCommand, SeedReconcileCommand, SeedValidateCommand, SeedViewCommand,
StashCommand, StateCommand, StatesCommand, SyncCommand, TrackingCommand,
UpdateCommand, WebCommand, WorkspaceCommand

### Missing Tests
- WebCommand — no tests at all
- EditCommand — no dedicated test (only integration)
- SaveCommand — no dedicated test (only variants)

### Missing MCP Tools
- All seed commands (new, edit, publish, reconcile, validate, view, chain, link)
- Query (`twig_query` — ad-hoc search for agents)
- Process (`twig_process` — process discovery for agents)

### Removed Commands
- **`status`** — absorbed into `show` (see context-commands spec)
- **`tree`** — merged into `show --tree` / `workspace --tree` (hidden alias kept)
- **`refresh`** — merged into `sync --pull-only` (hidden alias kept)
- **`states`** — renamed to `process` (expanded: types, states, fields)
- **`save`** — replaced by `sync` (push-on-write model)
- **`discard`** (top-level) — removed, only `seed discard` remains
- **`area`** — moved under `workspace area`
- **Flow commands** (start, close, done) — removed, violate process-agnostic principle
- **Git commands** (branch, commit, pr, link-branch, git-context, hooks) — removed, wrap git/gh

### Missing --output Support
- SeedChainCommand — no `outputFormat` parameter
- WebCommand — no `outputFormat` parameter

### Help Text Quality
- Audit pending — need to verify all commands have 2+ examples
  and thorough param documentation

---

## Functional Specifications

Command behavior specs are documented per-domain:

| Domain | Spec File | Status |
|--------|-----------|--------|
| Working Set & Sync | `docs/specs/working-set-sync.spec.md` | Draft |
| Context (set, show) | `docs/specs/context-commands.spec.md` | Draft |
| Mutation (state, update, note, edit, patch) | `docs/specs/mutation-commands.spec.md` | Draft |
| Tree/Query (show --tree, workspace, query, process, sync) | `docs/specs/tree-query-commands.spec.md` | Draft |
| Navigation (nav, up, down, next, prev, back, fore, history) | `docs/specs/navigation-commands.spec.md` | Draft |
| Seed Lifecycle | TBD | Not started |
| Navigation (up, down, back, forward) | TBD | Not started |
| Flow (start, close, done) | N/A | Removed |
| Git Integration (branch, commit, pr, etc.) | N/A | Removed |
