# CLI composition

## Entry point

[`Program.cs`](../../src/Twig/Program.cs) registers `TwigCommands` and the explicit
skills family through ConsoleAppFramework. Public command declarations generate
argument binding and syntax without reflection. `TwigConsoleApp` is the shared
production/test runner.

Startup performs platform, catalog, argument, output-format, and structured-target
admission before workspace/provider composition. Retired routes fail explicitly,
not by resolving a legacy command or opening input. Data commands without an
initialized Core marker refuse; they do not activate the old cache graph.

## Current execution

`CoreCommandAdapter` maps retained item, draft, query, sync, and proposal commands
onto the admitted Core workspace. Dedicated native command services own
initialization, authentication, connection inspection, user-wide Bench metadata,
planning rules, and process projection. Provider restrictions are additional
to command existence: a registered ADO convenience is not GitHub parity.

`CoreMutationAdmissionGuard` defines early catalog/provider restrictions.
`StructuredMutationArgumentValidator` prevents machine calls from selecting an
implicit active target. `SubcommandGuard` rejects misspelled compound verbs.
Refer to the [command guide](../commands/README.md), not historical declaration
comments or removed command classes, for the current supported surface.

## Rendering and help

`Twig.RenderTree` is a pure vocabulary; CLI renderers project it to terminal,
JSON, and documented minimal/ID output. Formats are explicit and validated;
support varies by command. Static views are not the retired live browser/TUI.

`ProgressiveHelp` provides offline task/group/leaf discovery. Syntax comes from
generated declarations. The [command reference](../commands/README.md) documents
behavior, effects, and failures separately from the short command-line help.
Skills are embedded separately from `.github/skills/twig` and are explicitly
installed or updated. See [help and skills](../features/skills.md).

## Errors and telemetry

Commands report usage, admission, provider, and storage failures rather than
returning a success-shaped empty result. Callers must inspect both the exit code
and complete structured output; a successful process does not replace journal
and provider verification.

Optional telemetry uses command metadata, generic counts/booleans, duration and
platform/version only. It must not contain work content, identities, provider
coordinates or process vocabulary, and it must not affect command outcomes.
See the telemetry rules in the [repository instructions](../../.github/copilot-instructions.md#telemetry--data-privacy).
