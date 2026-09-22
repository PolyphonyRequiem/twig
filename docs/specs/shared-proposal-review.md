---
status: implementation
tracked_in: [884]
---

# Shared proposal review and built-in CLI presentation

## Decision and scope

Approved after review of a running six-item prototype. Work item and decision provenance: [AB#884](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/884).

One canonical semantic review feeds independently shaped presenters. The default human CLI remains inline; it does not invoke the full-screen TUI. A host such as a Discord agent consumes structured review data and owns message formatting and interaction. Discord transport, new credentials, MCP redesign, and full-screen TUI review are not part of this implementation.

This spec records the approved behavior. Feature documentation and executable tests describe the shipped command syntax; they must not advertise a capability before the real command path implements it.

## Shared facts, independent layout

Extend `ChangeProposalReviewModel` and its existing builder. Do not create a competing proposal/diff authority. Existing `reviewModel` output remains the host contract; presentation styles or terminal cells must not leak into its semantic values.

The review provides:

- Exact proposal digest, workspace, ordered operations, preconditions, consequences, blockers and available authorization choices.
- Target identity/type/title/state and URL where known. Staged seeds retain their staged identity, with cached display title/type where available; no fabricated published ID.
- A bounded, explicitly labeled hierarchy context derived from known cache relations. Context-only items must not be counted as mutation targets. Cycles or missing nodes cannot silently discard operations.
- Field reference name and a readable display label, exact requested after value, and a before-value observation with its provenance. Unknown, known absent, empty string, and explicit clearing are different states.
- Deterministic change-size metadata for long field bodies where both sides are known. Metric name and units must be documented. The metric must be bounded in cost, preserve Unicode meaning, and must not claim to be a minimal edit script if it is not.

Preview remains cache-based; it must not refresh ADO once per item. A cache revision that differs from the operation's expected revision cannot be called the expected baseline. It is surfaced as unavailable/mismatched observation, never silently used for a trustworthy old-to-new diff. Exact requested effects remain visible even when the before value is unknown. Apply's existing preconditions remain authoritative.

Optional additive fields may retain model version 1 when old consumers remain safe. Changes in required safety semantics must not be smuggled through optional fields. Unknown model versions fail closed.

## Brief versus expanded review

Every mode retains coverage of every material consequence, blocker and available choice. The
structured model and JSON additionally retain exact operation identities, preconditions and
provenance; the human projection omits that machine bookkeeping rather than presenting it as
review content. Apply continues to enforce it.

Human review:

1. Omits the generic Details title, digest and workspace lines. Those remain in structured output
   for hosts and apply authorization.
2. Shows a recipe only when one exists and a rationale only when it is nonblank. Ad hoc and empty
   values are omitted rather than replaced with placeholders.
3. Shows actual field/link/seed/delete effects and any material observation warning. Ordinals,
   operation IDs, wire kinds, raw revisions, precondition values and cache provenance are not
   human content.
4. Shows affected identities with their owned changes together.
5. Shows scalar before-to-after effects; unavailable prior values are named as such.
6. Shows long description changes as an explicit field effect plus bounded character-change summary,
   with detail access. No full body dump is required by default.
7. Shows publication of a seed especially briefly: staged identity, title/type, target relationship
   and publication effect. It does not imitate edits on a nonexistent published item.
8. Keeps delete/link consequences, blockers and available authorization choices explicit. It does
   not replace a deletion with a neutral generic summary.
9. Retains a complete compact legend. Styling never substitutes for a textual effect.

Expanded human review exposes the exact values available in the same review object. JSON retains
exact structured data regardless of human density. A detail request within a review session must
not silently fetch a different baseline. A fresh preview is a fresh cache observation, not a claim
to historical immutability.

Support explicit expanded invocation and a review-only interactive detail loop. Interactive review must be opt-in and guarded for TTY input/output; redirected output must not unexpectedly wait for input. Details/Back/Cancel do not apply changes. Existing explicit apply authorization remains separate.

## Terminal visual contract

The accepted prototype supplies the visual target, not production architecture:

- Recognizable Twig type badges and state styling, including Nerd Font mode with Unicode/plain fallback.
- Work-item hierarchy guides only represent work-item relationships. Parent-to-child guides continue through the parent's changes and spacing.
- Each item's field changes occupy an inset, subtly shaded area, without a repeated `Changes` header or delta marker.
- Field labels and change values are divided by a quiet vertical separator. Width is derived from displayed content, bounded for narrow terminals. Same-depth sibling rows align; the separator continues through wrapped values.
- Space separates item groups; headers remain attached to their own edits.
- Long/wide/markup-like user content wraps or escapes safely. No ANSI/control-sequence execution from field data. No material effect is clipped out of review.

Reuse the existing render-tree/provider path. Add a small semantic layout vocabulary only where necessary. Do not copy the prototype's entire custom output traversal into each presenter.

Type/state appearance stays sourced once. Contrast is a foreground/background concern, not a second state-to-color map. Known surfaces can use measured contrast adjustment; unknown terminal surfaces require a safe documented policy rather than assuming the screenshot's dark color. Respect noninteractive/plain output. No system-font installation or terminal-theme mutation is required.

## Host/Discord contract

A host parses the structured preview and retains its exact digest and review observation. It may render item headings with links and quote blocks, or use its own native cards. It does not parse ANSI, interpret terminal tree glyphs as data, or depend on Nerd Font availability in a chat client.

An agent-written natural-language summary is additive, clearly identified, and derived from exact available fields. It cannot change metrics, claim missing values are known, suppress blockers, invent approval choices, or substitute its own digest.

Before presenting an approval action the host must have delivered the required review coverage. Long messages must be split or expanded without silently dropping the last operations. Unknown schema versions, truncated data and failed delivery are errors, not implicit approval. A chat reply or button must correlate the authenticated actor and decision with this proposal; details is never an apply action.

Twig remains the sole mutation authority. Host authentication, authorization provenance, duplicate decisions and transport failures must not create a parallel apply engine. Existing lifecycle validation/journal guarantees are retained, not reimplemented by the presenter.

## Verification commands

```sh
tools/run-tests.sh --pre-push
python3 -m unittest discover -s .github/skills/twig-review-presenter/scripts -p 'test_*.py' -v
python3 tools/verify-proposal-presenters.py --binary src/Twig/bin/Debug/net11.0/twig.dll
```

The smoke command creates a new private fictional workspace, configures its own
Nerd Font preference, seeds process icon/field metadata, and exercises the built CLI
plus the external JSON consumer. On POSIX it also exercises the real terminal
Details/Back/Cancel path and checks that no full-screen UI is entered. It never calls
apply or sync, inherits no credentials, and verifies the journal stays unconfirmed.
Use a fresh `--output <directory>` to retain an explicitly named evidence set.

Readable field labels are resolved once in the shared projection. If different
field references share a label, their effective labels include the reference;
unique labels stay compact. The metadata store itself is not rewritten.

## Acceptance

- Production CLI, not only a sample, exercises the shared enrichment and visual projection.
- Model/JSON/CLI tests cover set, clear, empty, missing item, revision mismatch, seed, link and delete effects; digest and action lists are unchanged.
- Brief tests prove long text is absent without deleting its field effect; expanded/JSON tests prove exact data is available.
- Render tests cover long titles, narrow widths, shared label alignment, guide/divider continuation, glyph modes and non-color meaning.
- Input tests cover details/back/cancel, unsupported versions, invalid input and noninteractive behavior without applying.
- Public API/serialization metadata and command documentation match the implementation.
- A host presenter reference is distributed alongside operational guidance, with exact values kept separate from optional agent summaries.
- Existing proposal authorization, lifecycle and command regression suites pass. Verification records actual command output and limitations.
