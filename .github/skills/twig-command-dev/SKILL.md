---
name: twig-command-dev
description: Current CLI command development standards for Twig, including targeting, help, source-generated JSON, errors and bounded validation.
---

# Twig command development

Keep the supported catalog explicit. Do not add a compatibility handler,
presentation-only option, alternate publisher or dormant integration merely
because historical documentation describes one.

- Use ConsoleAppFramework declarations for syntax/defaults. Update
  `docs/commands/index.json` and the corresponding bundled Markdown behavior and
  examples with each supported command change. Do not add a parallel manual
  syntax/example catalog.
- Follow `CoreCommandAdapter` and the existing composition modules; no legacy
  Domain repository or pending-writer graph is a fallback.
- Keep structured targeting explicit and validate arguments before workspace,
  provider, input or filesystem side effects.
- Return repository-standard typed errors and nonzero exit codes. Missing,
  incomplete, unsupported and indeterminate must not become empty success.
- Use source-generated JSON and AOT-safe types. Keep telemetry within its
  allowlist and never emit provider/work-item identifiers or content.
- Discover process fields, types, transitions and gates from the provider.
- Preserve static human output and supported structured shapes. Full-screen
  UI, captured rendering envelopes and old OMP/Copilot adapters are retired.
- Do not require MCP parity for CLI changes. Current daemon MCP is a deliberately
  bounded read-only host surface, not a mirror of CLI mutations.
- Cover changed behavior at the real composition boundary, proving a regression
  fails before the fix. Follow `AGENTS.md` for serial bounded build, focused
  tests and offline smoke; broad suites are release-candidate work.
