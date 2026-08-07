---
id: 0023
title: How does a caller name its Context?
type: question
blocked_by: [0022]
---

## Question

0022 ruled that a Context is a **disposable place to stand**, opened by a caller and closed
when done, and that stage 1 gives every caller its own Context — removing the single shared
`active_work_item_id` row that 0021 could only patch at one surface.

It did not say **how a caller names which Context it is in**. That is the last thing blocking
stage 1 from being handed off, and it is a **contract** question: whatever is chosen, every
script anyone has written is affected.

## Why this is a research ticket, not a grilling

Owner's call (2026-08-06): *"better answered by scenario analysis and research."* The taste
question at the end is one line — which default. Everything before it is enumerable evidence,
and asserting a default without that evidence is guessing at a contract that cannot be changed
quietly afterwards.

## The narrow question

When a script runs twig twice in a row, does the second invocation land in the **same** Context
as the first by default, or does each invocation start **clean** unless told which Context to
join?

Both have a real failure mode:

- **Sticky by default** — run two silently inherits run one's state. This is the *current*
  behaviour generalised, and it is the class of defect 0022 exists to remove.
- **Clean by default** — a human at a terminal loses their place between commands unless the
  shell is special-cased, which reintroduces a per-surface rule 0022 deleted.

## What to research (bar: cite, do not assert)

1. **Enumerate the scenarios**, with a concrete command sequence for each, and state what each
   addressing option does to it:
   - a human at a terminal running several commands in a row;
   - a script running twig N times in one job;
   - two scripts running concurrently against one Connection;
   - an agent doing a task, then a second agent doing another;
   - CI, where there is no prior state and nothing to inherit;
   - the TUI, which is one long-lived session with many commands;
   - a targeted command (names its own work item) — which per 0022 needs **no** Context at all,
     and must not create or mutate one as a side effect.

2. **Enumerate the mechanisms**, with prior art from tools that solved the same problem —
   ambient session state addressed across process boundaries. Candidates to examine rather than
   assume: an environment variable naming the Context; an explicit flag per invocation; derive
   from process ancestry / terminal session; a default Context per Connection; explicit
   open/close returning a handle. For each: what it does to each scenario above, and how it
   fails when the caller forgets.

3. **The lifetime questions 0022 banked as open**, which this ticket cannot avoid touching:
   - what reclaims a Context a caller never closed;
   - whether closing a Context holding **unpushed edits** is refused, or the edits simply stay
     pending against the Connection. It must not silently discard — that is the #271 class.

4. **Migration.** What happens to callers written against today's implicit single slot. Per
   0001 twig is a single-user local tool, so there is no version skew to manage — but a script
   in a repo is still a caller, and silently changing what it targets is the 0021 defect wearing
   a new hat.

## The finding that settles the shape (2026-08-06)

Research landed in two memos:
[`assets/context-addressing-priorart.md`](../assets/context-addressing-priorart.md) and
[`assets/context-addressing-scenarios.md`](../assets/context-addressing-scenarios.md).

**Prior art splits cleanly into two families, and the split is about FAILURE, not ergonomics.**

| Family | Reference is | On a stale reference | Examples |
|---|---|---|---|
| **Handle** | a resource that must resolve | **fails loud** | `docker context` (`DOCKER_CONTEXT=nonexistent` → hard error [measured]); `ssh-agent` (`SSH_AUTH_SOCK` → `ENOENT` [measured]) |
| **Name** | a mutable name in a shared file | **acts on the wrong target, silently** | `kubectl current-context`; `terraform workspace`; `gh` active account |

**twig today is the second family, structurally.** The active work item is one row in a shared
table. **A row always resolves.** There is no state in which twig can notice the reference is
stale, because staleness is not representable — which is exactly why 0021 had to fix this at
the MCP surface with a *rule* (every tool names its target) rather than with a *mechanism*.

Two corroborations worth keeping:

- **Regret is measurable as ecosystem mass.** `kubectx` / `kubens` / `kube-ps1` exist only to
  make kubectl's invisible global visible; `gh` users bolt `direnv` on to fake per-directory
  accounts; "workspaces are a trap" is HashiCorp's own steer. Nobody writes a prompt plugin to
  show you your `DOCKER_CONTEXT`.
- **The defect class reproduced live** [measured]: `TMUX=/tmp/bogus,999,0 tmux new-session -d`
  exits **0** and creates the session on the bogus socket, invisible to a plain `tmux ls`.
  tmux trusts the path and never validates the pid/session-id it carries.

### RULING — twig takes the handle family. An unknown or expired Context id is a HARD ERROR.

Not a fallback, not a warning, not a silent fresh Context. Non-zero exit, name the id, say
what to do.

**Rationale.** This is the ONE choice that separates docker's model from the three regretted
ones. Everything else in this ticket — transport, defaults, lifetime — is ergonomics layered on
top, and none of it can rescue a design that cannot represent staleness. Choosing "name" and
then adding warnings reproduces kubectl and hands the user a prompt-plugin problem.

**Consequence, stated so it is not softened later.** A caller that holds a Context which has
been reaped gets an error rather than quiet reassignment. That is the point: **being wrong
loudly is the feature.** The alternative failed for kubectl in production often enough to spawn
a tool ecosystem.

**Guard.** The hard error must never be reachable in a way that risks pending edits. It cannot
be, by construction — the pending set belongs to the **Connection**, not the Context (§below),
so a refused command has nothing to lose.

### Corollary — close never refuses, and never gets a `--force`

The pending set is Connection-owned. Closing a Context therefore **structurally cannot discard
work**, so refusing on unpushed edits would be ceremony that teaches the user nothing.

**Close exits 0 and reports what remains pending.** Discard stays a separate, explicit verb on
the pending set.

🔴 **No `--force` on close.** A force flag that is habitually needed is how #271 recurs: the
habit outlives the reason, and the one time it matters the user types it reflexively.

## Output — DELIVERED

A memo under `wayfinder/assets/` with the scenario × mechanism matrix, the prior art cited, and
a **ranked recommendation with one named falsifier**. The final pick is the owner's; the memo's
job is to make it a one-line call rather than a debate.

## Explicitly out of scope

- Implementing anything. 0022 stage 1 is blocked on the ANSWER, not on this ticket's prose.
- Re-opening 0022's rulings. The Bench/Context split, shared Bench view, and staged order are
  settled.
