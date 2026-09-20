---
name: twig-review-presenter
description: Present Twig proposal reviews in a host such as Discord while preserving exact effects, proposal identity, blockers and authorization choices.
---

# Twig review presenter

Use alongside `twig`, not instead of it. This skill owns presentation, not mutation,
work-item discovery, credential handling, or approval policy. It is portable guidance:
no host plugin or Discord credentials are bundled or required.

## Obtain the review

1. Resolve the intended Twig workspace and validate the proposal through Twig.
2. Run `twig proposal preview --file <path> --output json` in that workspace and parse
   the result as JSON. Preview can update the local proposal journal; it does not apply
   work-item changes.
3. Require a `reviewModel` with `model = "twig.change-proposal.review"` and a supported
   `modelVersion`. This presenter understands version 1. Unknown additive members can
   be ignored; an unknown version is a refusal, not permission to improvise.
4. Retain the exact `digest`, workspace, complete model and preview result. Refuse a
   missing model, mismatched outer/model digest, truncated JSON, or an unsupported
   operation/consequence kind. Never reconstruct a digest from rendered text.

## Present the same facts, not the same pixels

- Show workspace/scope once. Use item type, ID, title and a verified item link.
- Keep hierarchy context visibly separate from field effects. Label context-only
  items; do not count them as modified items. Keep seed identity distinct from a
  published ID.
- Group effects with their owning item while retaining declared operation order and
  identifiers. If grouping would imply a different execution order, expose the order.
- Preserve **every operation, precondition, material consequence, blocker and allowed
  authorization choice**. A concise review is not a partial review.
- Display exact old/new scalar values when the baseline is known. Treat unknown,
  absent, empty and cleared values distinctly. A cache observation with the wrong
  revision is not the operation's expected baseline.
- Use supplied description metrics with their supplied units/algorithm. Do not
  recompute them with a host-specific diff algorithm. Unknown metrics stay unknown.
- Description bodies may be deferred to detail; their field effect and available
  change summary remain visible. Delete/link/publication effects are never hidden.
- Agent-written summaries may explain long text, but label them as summaries and
  keep exact values available. Do not manufacture meaning from a missing baseline.

## Discord presentation

Prefer native Markdown that wraps on a phone:

- One summary and a short context statement.
- One bold linked item heading, with type name and a restrained emoji or host icon.
- Quote-block lines for field effects. No Markdown tables or ANSI-dependent layout.
- Blank lines between items; no repeated `Changes` heading.
- One detail affordance and, only when required and allowed, a decision prompt.

Escape field values as untrusted text. Suppress unintended mentions and link previews
through the host's supported message controls; user-authored text must not become a
command, mention, executable control, or forged approval UI. Prefer text labels over
color-only state, and do not rely on the recipient having a Nerd Font.

Size before sending. Split long reviews at item/operation boundaries and identify
continuation messages. Never clip away later operations or assume a failed send was
seen. Required coverage must be delivered before soliciting approval. For huge exact
values use a detail message or downloadable text artifact, with its proposal identity.

## Details and decisions

A request such as `details 104` selects content from the retained review object;
it does not apply, approve, or silently replace the baseline with a fresh preview.
If the retained data is unavailable, explicitly refresh the review and disclose that
it is a new observation before continuing.

The host authenticates the responding actor and correlates a decision with the exact
workspace and proposal digest. Display only choices supplied by Twig. Do not offer
Apply when absent. Do not infer approval from opening details, silence, a timeout,
a renderer error, or failed message delivery.

Whether explicit human approval is needed is policy, not a side effect of displaying
a proposal. An authorized agent workflow may proceed under its existing authority;
a human-steered workflow waits for its required human decision. No renderer may
upgrade that authority.

Apply through the documented Twig lifecycle, not direct per-field writes or host-side
ADO calls. Inspect the structured result and journal, including partial failures,
and verify actual outcomes before announcing success. Duplicate button/reply events
must not start independent mutation attempts.

## Built-in CLI and future TUI

The built-in CLI is the guaranteed fallback and uses the same semantic model. A rich
inline terminal view is not a full-screen TUI. Full-screen TUI launch is explicit;
this skill must not require it for JSON/Discord review or silently invoke it.

## Offline reference consumer

`scripts/render_discord_review.py` is a dependency-free Python reference adapter.
It reads saved preview JSON and emits Discord message payloads without sending them:

```sh
python3 scripts/render_discord_review.py preview.json > messages.json
python3 scripts/render_discord_review.py preview.json --full > full-messages.json
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Run from this skill directory, or use absolute script paths. A host must separately
send the returned `messages` in order and verify delivery; `allowed_mentions` disables
mentions and `flags` suppresses embeds. This is not a bot, transport plugin, or approval
handler. It intentionally retains operation/precondition labels rather than imitating
an agent-written summary. It accepts the enriched v1 before-observation/textChange
members and falls back to “before unavailable” for legacy v1 data.

## Review checklist

- Same digest/workspace and all operations covered?
- Before-value provenance and unavailable data represented honestly?
- Blockers, destructive effects, preconditions and exact choices visible?
- Long text expandable without silently changing the review?
- Host-specific escaping, message length and delivery checked?
- Rendering and applying remain separate?
