---
name: twig-cli
description: Use when operating Twig to inspect, query, or navigate work items; create or publish seeds; change work items; run proposal lifecycle; or sync, configure, authenticate, and manage workspace or Bench views.
---

# Twig CLI

Use Twig through its current source-derived documentation. This skill carries the
operating model; the command reference owns command syntax, flags, defaults, and
exit behavior.

## Route before acting

| Need | Read first |
|---|---|
| Exact command, flags, side effects, or aliases | [Command reference](../../../docs/commands/README.md) |
| Workspace, Bench, active-item pointer, or Context terminology | [Workspace, Bench, and Context](../../../docs/features/workspace-bench-context.md) |
| Local drafts, validation, publishing, or reconciliation | [Seeds and publishing](../../../docs/features/seeds-and-publishing.md) and [seed commands](../../../docs/commands/seeds/README.md) |
| Reviewable ADO mutation through the proposal journal | [Plans and proposals](../../../docs/features/proposals.md) and [proposal commands](../../../docs/commands/plans/README.md) |
| Token acquisition, sign-in, cache recovery, or PATs | [Authentication](../../../docs/features/authentication.md) |
| Type, state, field, transition, or layout discovery | [Process commands](../../../docs/commands/process/README.md) and [Process description](../../../docs/features/process-description.md) |
| Profile pin or process-policy resolution | [Reference profile](../../../docs/features/reference-profile.md) |
| Explicit TUI, MCP, or Oh My Posh use | [Experimental commands](../../../docs/commands/experimental/README.md) |

## Invariants

- **Discover, do not assume.** Work-item types, states, fields, transitions, and
  layouts come from the target process at runtime. Read the process reference
  before choosing a type, state, or field for an unfamiliar project.
- **Read `mutates` before executing.** `none` is read-only; `local` changes only
  local workspace/cache/pending state; `ado` can write Azure DevOps. Treat this
  field in the command index as the safety boundary.
- **`twig set` is a local pointer.** It neither claims work nor syncs nor changes
  a Bench. Claim-like board changes require the documented mutation path.
- **Seeds are local until published.** Validate before publishing; use the seed
  reconciliation path after an interrupted publish rather than inventing repair.
- **Proposals are auditable.** Use canonical `proposal` commands. Validate, then
  preview, then apply only with the previewed digest and required authorization;
  inspect the journal/result afterward. `plan` is a retained deprecated alias.
- **Protect credentials.** Never place PATs, access tokens, refresh tokens, or
  token-cache contents in command arguments, logs, notes, or issue text.

## Operating loop

1. Classify the request with the table above and read its pointer.
2. Select the canonical command; use an alias only when compatibility requires it.
3. Check its `mutates` value, required context, and failure behavior.
4. Select an output format for the consumer. Use JSON only when the caller will
   parse it; use the documented human or minimal format when that is the contract.
5. Execute the command and inspect its result. Verify a mutation through the
   documented read/refresh or proposal-status path before claiming success.

## Terms

- **Connection:** one Azure DevOps `{org}/{project}` endpoint and local mirror.
- **Bench:** a durable, named backlog view made from selectors.
- **Active-item pointer:** the current CLI-local target changed by `twig set`;
  never a work claim.
- **Seed:** a negative-ID local draft work item.
- **Proposal:** a declarative mutation file with a digest-keyed journal; canonical
  replacement for the older `plan` name.

For any detail not named here, start at the [command reference](../../../docs/commands/README.md) rather than inferring syntax or behavior.
