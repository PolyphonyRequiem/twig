# Handoffs

Procedures that **a person must run**, because no agent and no CI job can.

A handoff lives here when the blocker is access to hardware, an operating system, a
network, or an account that the automated lanes do not have. Each file is written to be
opened cold on the target machine, with no session context and nothing else read first.

Conventions:

- **Self-contained.** Copy-pasteable commands, prerequisites checked before use, and the
  expected output written down. Assume the reader has forgotten why this exists.
- **Name the pass bar explicitly**, and say what a *false* pass looks like. The Windows
  check exists precisely because a clean compile is not a pass.
- **Say where the result goes** — an issue number, a ticket, or both.
- **Link from the ticket that is gated on it**, so the handoff is reachable from the work
  rather than only from this directory.

| File | Runs on | Answers |
|---|---|---|
| [windows-native-tui-check.md](windows-native-tui-check.md) | Windows, owner's hardware | Can the TUI be compiled natively on Windows? Gates [ticket 1002](../../wayfinder-1.0/tickets/1002-fold-the-tui-into-one-binary.md) / issue #359. |
