---
id: 1005
title: Does twig need a server, and what would notifications actually be?
type: decision
status: open
blocked_by: []
---

## Question

Today every twig surface is a short-lived process against a local SQLite cache, and every
consumer that wants to know something changed has to ask. Should twig grow a long-running
local process — `twig serve` or a daemon — that owns the cache and pushes change
notifications to its surfaces?

**This is a decision ticket, not a build.** The output is an answer plus the reasoning, and
if the answer is yes, a follow-up execute ticket. It is **not a 1.0 blocker** unless the
answer turns out to be that the TUI cannot do its job without it — which is the one branch
that would pull it forward.

## Why this came up

During the Windows AOT verification (#359) a `twig-tui.exe` left running held
`.twig/**/twig.db` open, and cleanup of the scratch clone failed with *"Device or resource
busy"* on the db, `-shm` and `-wal` files.

**That specific symptom is not evidence for a server**, and the ticket should not pretend
it is. It was Windows refusing to *delete* a file with an open handle, which only bites
during cleanup and never during normal use; the same `rm` succeeds on Linux. Concurrency
itself is already handled: `SqliteCacheStore.cs:113-117` sets `journal_mode=WAL` and
`busy_timeout=5000`, so readers do not block the writer, the writer does not block readers,
and concurrent writers serialise with a 5-second wait. Background agents reading twig's
cache while an interactive TUI is open is the case WAL exists for.

The real question the incident surfaced is the second half: **twig has no way to tell
anyone that something changed.** Every consumer polls or re-syncs.

## What is already established

- **There is no self-servable event source from ADO. Confirmed, not assumed.**
  (`wayfinder/tickets/0001-what-is-twig-for.md` §7.) Personal notification subscriptions
  filter by *work item fields* only — a saved query or WIQL cannot be the trigger. Service
  hooks, the only machine-consumable channel, require *"Edit subscriptions"*, and by
  default only project administrators hold that. The owner is a plain Contributor.
  **Polling is structural.**
- **Consequence for this ticket, and it is the load-bearing one:** a twig server could not
  subscribe to anything upstream. It could only *centralise the polling* — one poller
  instead of N — and then fan out locally. That is a real benefit but a much smaller claim
  than "twig gets notifications," and the ticket must not slide from one to the other.
- **The good polling primitive already identified:** `GET
  /_apis/wit/reporting/workitemrevisions` with a persisted `continuationToken`. The token
  is a watermark, so it is clock-free — no skew, no time-window guessing. Already flagged
  as input to the reconciliation module (architecture map 0004).
- **Cost baseline:** twig ships AOT binaries with fast startup precisely because the
  surfaces are short-lived. A daemon inverts that model, and brings lifecycle
  (start/stop/upgrade/crash), a transport, and a "which version is running?" problem. #1002
  is currently *removing* a binary from the shipped set; this would add a mode.

## What to decide

1. **Is there a real problem with N concurrent short-lived processes**, beyond the
   Windows delete-handle artifact? Wanted: an observed failure, not a hypothesis. Contended
   writes under real agent load are the place to look, since that is the only path WAL
   serialises.
2. **What is a notification FOR, concretely?** Name the consumers. A TUI redrawing when a
   background sync lands is a different feature from a background agent waking on someone
   else's edit, and only the second needs a server. If the only consumer is the TUI, an
   in-process file watch or a poll on the same cache is far cheaper.
3. **Could the benefit be had without a daemon?** Options that should be priced before a
   server is chosen: SQLite change notification on the existing WAL file, an advisory file
   watch, or a `twig watch` foreground command that polls the revisions endpoint and prints
   to stdout — which composes with agents the way a daemon does not.
4. **If yes to a server: what does it own?** Cache only, or auth token cache and sync
   scheduling too? Whatever it owns becomes a thing that must be running for surfaces to
   work, and that is the decision's real cost.

## Notes

- **Do not fold this into #1002.** That ticket is about packaging one binary and is nearly
  unblocked; this one could add a mode to it. Keeping them separate keeps 1002 shippable.
- Prior art in the repo worth reading before deciding:
  `docs/projects/twig-vscode-extension.plan.md` and
  `docs/projects/sdlc-preflight-check.plan.md` both touch long-running/notification shapes.
- The MCP surface is a likely consumer and MCP is **out of 1.0** by decision, which is part
  of why this is not a 1.0 blocker.

## Answer

<!-- empty until resolved -->
