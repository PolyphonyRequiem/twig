---
id: 0003
title: How a caller asks for it, and what Twig promises publicly
type: grilling
status: open
blocked_by: [0002]
claimed_by:
---

> 🔴 **SUPERSEDED — tracked on the board as [#221](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/221).**
> Do not edit or re-sync this file. Kept for git history only.

## Question

Two coupled decisions, deliberately in one ticket because answering either alone re-opens
the other.

**How does a caller ask?** Three shapes:
- enrich the existing `twig process <type>` output in place;
- add an output mode, `-o descriptor`;
- add a verb, `twig process describe <type>`.

Enriching in place changes what every existing caller receives, and the existing four-key JSON
is already consumed. Note that `twig process layout <type>` already exists as a separate verb,
so a descriptor verb has precedent — but two verbs reading overlapping data is also how you end
up with two answers to the same question.

**What becomes a public promise?** Surfacing rules and layout means promoting `ProcessRule`
(with `RuleCondition`/`RuleAction`) and, if the descriptor embeds layout structurally rather
than by reference, more of the `FormLayout` surface. Both are `internal` **deliberately** —
`ProcessLayoutCommand`'s remarks state `FormLayout`'s shape is still under design and freezing
it now makes it harder to correct.

A JSON output shape is a promise even when no C# type is public. Decide whether this descriptor
is a **stable contract** or an **explicitly unstable diagnostic**, and say so in the output
itself if the latter.

The adjacent map settled the mechanism — SemVer over `PublicAPI.Shipped.txt`, entries promoting
from `Unshipped` in one commit at release. That is settled input; do not re-derive it. What is
open is *whether these particular types go through it now*.

## Answer

<!-- empty until resolved -->
