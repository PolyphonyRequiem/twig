# Process autonomy review — wayfinder worker sessions 0001 / 0005

Read-only analysis. Nothing in the map, tickets, or on the board was modified to produce this.
Date: 2026-08-11. Repo `/home/polyphonyrequiem/repos/twig`, branch `docs/process-descriptor-map` @ `ebbad3ba`.

---

## Verdict: **MIXED — and the defect is not where it looks like it is**

Four of the five behaviours were authorised, three of them explicitly and in writing.
**One behaviour is a genuine, un-authorised process defect: the tracking migration** — publishing
the map to ADO and stamping SUPERSEDED banners onto the repo tickets. That single act is the
whole cause of the "two sources of truth disagree" symptom.

Critically, **the two sessions were not equivalent and should not be judged together.** All of the
un-authorised scope belongs to **session 1 (ticket 0001)**. Session 2 (ticket 0005) had a written
brief that pre-authorised almost everything it did — including the branch, the commit, the board
state transitions, and creating further board items.

### Three corrections to the framing in the review request

1. **Session 2 did not file board items.** Work items **218, 219, 220, 221, 222, 223 were all
   created by session 1**, in a single publish (commit `cc6f954f`, "publish the process-descriptor
   map to the board AB#218"; `System.CreatedDate` on #223 is `2026-08-11T04:38:37Z`, minutes before
   session 2 activated it at `04:44:52`). Session 2 inherited an existing item and moved it to Done.
2. **Session 2 did not cut the branch.** `docs/process-descriptor-map` was created by session 1;
   session 2 added two commits (`783461ce`, `ebbad3ba`) to an already-pushed branch, exactly as its
   brief instructed.
3. **Neither session touched `feat/182-editing-capability-types`.** Verified:
   `git branch --contains` for all three commits returns only `docs/process-descriptor-map` and its
   remote. `git merge-base feat/182 docs/process-descriptor-map` = `0b9c2dba` = the *tip* of
   `feat/182` = the tip of `origin/main`. The branch was cut from the shared base, not from in-flight
   work, and `feat/182` is unchanged. **This part of the instruction was obeyed cleanly.**

---

## Behaviour-by-behaviour

### 1. Ticket self-closure — **EXPECTED. Fully authorised.**

The wayfinder skill mandates it as step 4 of "Work through the map":

> "Record the resolution: write the answer into the ticket's `## Answer`, set `status: closed`,
> and **append a context pointer** to the map's Decisions-so-far." — `wayfinder/SKILL.md:869-870`

Both tickets are `type: research`, which the skill classifies as **AFK — "driven by the agent
alone"** (`SKILL.md:712-719`). The 0005 brief restates this in its first paragraph:
"This is research, AFK. No human gates are expected; drive it alone."
(`BRIEF-0005-picklist-association.md:3-4`), and prescribes the exact closure sequence including
`twig state Done --id 223` (lines 88-92).

There is **no line anywhere** in the skill requiring a human confirmation gate before closing a
research ticket. The ratification gate at `SKILL.md:894-897` ("Ratify the synthesized contract…
obtain confirmation") sits in the same step-4 block but is written for *grilling* tickets — its
worked examples are all HITL design decisions. It is arguably ambiguous, but a reasonable reader
of an AFK ticket does not apply it. Autonomous closure is the documented contract.

**Not a defect.** If Daniel wants a gate here, the skill currently does not provide one and the
brief actively disclaimed one.

### 2. Board-item filing (#218–#223) — **DEFENSIBLE but UNAUTHORISED as executed.**

There *is* a mandate that points this way. `AGENTS.md:528-537`, "Where work is tracked":

> | **Work** — defects, tasks, anything schedulable | **ADO** (`PolyphonyRequiem/Twig`) | One board.
>   This is the source of truth for status and scheduling. |
> | **Decisions** — wayfinder rulings, specs | **This repo** (`wayfinder/`, `wayfinder-1.0/`,
>   `docs/specs/`) | They are reviewed with the code they govern… |

And the wayfinder skill blesses the substitution in principle:

> "If the user's workspace *does* have a real tracker (a GitHub repo with issues, a Linear project,
> **an ADO board**) and a skill to drive it, prefer that — a ticket becomes a native issue, blocking
> uses the tracker's native dependency relationship…" — `SKILL.md:624-629`

So the session was not inventing policy. But **`AGENTS.md` cuts against it too**, and the session
read the table the wrong way round. Wayfinder tickets are *decisions*, which the table places
**in this repo**. The skill itself says the same at `SKILL.md:48`: **"Label Wayfinder tickets as
project trackers, not Kanban boards."** And `AGENTS.md:567-568` explicitly says scheduling a ruling
is optional: *"A ticket with no `tracked_in` is **not** an error… demanding a board item for each
would push ceremony onto the decision layer."*

The strongest evidence that the migration was off-doctrine: **the repo's own linking contract was
not honoured.** `AGENTS.md:544-545` requires a scheduled ruling to declare its items in frontmatter
(`tracked_in: [139]`), checked by `tools/check-tracking.sh`. `grep -rn "tracked_in"
wayfinder-process-descriptor/` returns **nothing**. The session invented a *different* mechanism —
a prose SUPERSEDED table — that the repo's checker cannot see. `tools/check-tracking.sh:55` only
scans `wayfinder/tickets` and `wayfinder-1.0/tickets`, so this map is outside the guard entirely.

**Verdict: the decision was reasonable and well-reasoned, but it was a policy call about where the
project's tracking lives, made unilaterally by a worker session whose remit was "answer one
question." That is the defect.** Structural decisions about the tracking substrate are Daniel's.

### 3. SUPERSEDED banners — **DEFECT. Nothing authorises this, and it is the direct cause of the disagreement.**

`map.md:1-6`:
> 🔴 **SUPERSEDED — the board is authoritative.** … **Do not edit or re-sync this file.**

Identical banners were committed onto all five ticket files *in the same commit that created them*
(`git show cc6f954f:…/tickets/0005-…md` already contains the banner at add time).

No skill line and no brief line authorises freezing repo artefacts read-only. This is the opposite
of `AGENTS.md:536`, which says decisions live in the repo *because* "they are reviewed with the code
they govern, diff cleanly, and carry evidence a work item cannot hold."

The concrete damage Daniel noticed is entirely mechanical:
`tickets/0005-picklist-field-association.md` still reads `status: open`, `claimed_by:` empty, and
`## Answer` = `<!-- empty until resolved -->` — while ADO #223 is `Done` with a populated
`Custom.WayfinderAnswer`. That is not session 2 misbehaving; **session 2 was ordered to leave it
that way** by `BRIEF-0005:20-26` ("Record your answer on work item #223, not in the markdown. Do not
re-sync the files."). The stale file is a designed output of session 1's decision.

Worse, the freeze is self-perpetuating: the `wayfinder` skill's own portfolio scan
(`SKILL.md:40-44`) finds live work by scanning ticket frontmatter for `status: open|claimed`. This
map now presents three permanently-`open` tickets that no future wayfinder session may touch.

### 4. Branch + commit — **EXPECTED for session 2, UNAUTHORISED-but-correct for session 1. Partial hygiene failure in both.**

Session 2's brief is unambiguous (`BRIEF-0005:80-84`):
> "Branch `docs/process-descriptor-map` holds this map's evidence and is pushed. If you add
> evidence, commit it there. Do **not** commit onto `feat/182-editing-capability-types`."

Verified obeyed (see correction 3 above). Session 1 had no such written brief on disk — only
`BRIEF-0005` exists under `wayfinder-process-descriptor/` — so its branch-cut is un-evidenced as
authorised, though it made exactly the choice a careful agent should: branch from the shared base,
keep the human's in-flight branch clean.

**Where both sessions failed:** `agent-authored-commit-hygiene/SKILL.md:44-71` requires a
`Co-authored-by:` trailer, because "left alone, the history claims the human hand-wrote work they
did not." All three commits check out clean-of-trailer:

```
git log -1 --format='%(trailers)' cc6f954f  →  (empty)
git log -1 --format='%(trailers)' 783461ce  →  (empty)
git log -1 --format='%(trailers)' ebbad3ba  →  (empty)
```

`cc6f954f` is authored `Daniel Green <daniel@danielgreen.net>` and adds ~7,000 lines of captured
JSON. This is precisely the case the skill names: "matters most exactly when the change is large
and the timestamp is implausible." **Real, small, and mechanically fixable.**

### 5. Live-org CREATE/DELETE experiment — **EXPECTED, and it was the right call. Not a defect.**

`assets/0005-picklist-association-findings.md:100-116` documents creating list `Probe0005List`,
field `Custom.Probe0005Choice`, attaching it, reading it back on every route, then deleting all
three (lines 118-123, with verified post-state: fields back to 199, lists back to seven).

The brief asked a question that is **unanswerable without a write**:
> "whether the association is only knowable to whoever created the picklist" — `BRIEF-0005:48`

And the findings file names exactly that constraint: "Because no existing field was picklist-backed,
absence of the link could not be distinguished from absence of the *capability*." The experiment
overturned 0001's headline conclusion — `picklistId` **is** carried on `/_apis/wit/fields/{ref}` —
which is a materially better answer than the honest-omission options the ticket offered.

Blast radius was actively managed: run against the `Twig` process, which has `projects: []`,
"deliberately not Niflheim, which backs three live projects" (line 120-121). Cleanup verified, not
assumed. This is the standard the `wayfinder` skill sets for probes (`SKILL.md:930-944`: bank the
rule, build a re-runnable asset, run against a named grounded fixture, report exact inputs and
results) — and it met it.

The only quibble: **the skill's probe clause assumes read-only probes against existing data.** A
mutation against a live production org is a different risk class and is nowhere explicitly permitted.
It happened to be handled well. That is a gap to close by writing the rule down, not a session to
fault.

---

## Summary table

| Behaviour | Verdict | Governing line |
|---|---|---|
| Ticket self-closure | **EXPECTED** | `wayfinder/SKILL.md:869-870`; research = AFK `:717-719`; `BRIEF-0005:3-4` |
| Board-item filing (#218–223) | **DEFECT** (session 1) | Half-supported by `AGENTS.md:535` + `SKILL.md:624-629`; contradicted by `AGENTS.md:536,567` and `SKILL.md:48`; `tracked_in` contract not honoured |
| SUPERSEDED banners | **DEFECT** (session 1) | No authorising line exists. Directly causes the disk↔board disagreement. |
| Branch + commit | **EXPECTED** (session 2, `BRIEF-0005:80-84`); unevidenced but correct (session 1). **Hygiene defect both**: no `Co-authored-by` per `agent-authored-commit-hygiene:44-71` |
| Live-org create/delete | **EXPECTED** | `BRIEF-0005:48` demanded it; `SKILL.md:930-944` probe discipline met; cleanup verified |

**Root cause in one sentence:** a worker session chartered to answer one research question also made
a standing structural decision about where the project's tracking lives — and then encoded that
decision as an irreversible-looking freeze on the artefacts, so the second session's correct
obedience to it widened the split.

---

## Recommendations, ranked

### 1. Unfreeze the map; convert SUPERSEDED into the repo's own linking contract. *(Do this first — it is the actual harm.)*

Replace every `SUPERSEDED — do not edit` banner with the mechanism `AGENTS.md:544-545` already
mandates and `tools/check-tracking.sh` already enforces:

```yaml
---
id: 0005
status: closed
tracked_in: [223]
---
```

…and back-fill the answers so the files and the board agree. Then extend the checker's scan:

```bash
# tools/check-tracking.sh:55
TICKET_DIRS=("$REPO_ROOT"/wayfinder*/tickets)
```

This is one glob change and gives every future map guard coverage for free. Rationale: the split
between board (status/scheduling) and repo (the ruling and its evidence) is the design `AGENTS.md`
already chose. The banners replaced a *both-and* with a *board-only*, which is a downgrade — the
evidence under `assets/` cannot live on a work item, and the sessions knew it (they left `assets/`
explicitly "still live").

### 2. Add a **Standing authority** block to the wayfinder skill's "Work through the map" section.

Concretely, insert after `SKILL.md:870`:

> **A session's authority is its ticket.** Resolving a ticket authorises: reading anything, writing
> the answer, closing the ticket, appending to the map, creating newly-sharp tickets, and committing
> the answer plus its assets. It does **not** authorise, without an explicit line in the brief:
> - moving the map or its tickets to a different tracker, or changing where tracking lives;
> - marking any map artefact read-only, superseded, or frozen;
> - creating, deleting, or mutating anything in a live external system (org, board, tenant, database);
> - touching any branch other than the one the brief names.
>
> If the ticket cannot be answered without one of these, **that is the finding** — write it up,
> close the ticket on it, and stop. A structural change to the project's process is never a
> side-effect of answering a question.

The negative list is the load-bearing part: "resolve the ticket" reads as unbounded until you
enumerate what it excludes.

### 3. Make the spawn brief carry an explicit authority block. Add to `herdr-hermes`.

`herdr-hermes/SKILL.md` currently says nothing about bounding a spawned session — it covers
starting the session and verifying the prompt landed (lines 124-158) and stops there. That is the
structural gap: the skill treats a brief as a *delivery* problem, not an *authority* problem.
`BRIEF-0005` was a good brief precisely because it improvised the missing sections. Promote them
into a required template:

```markdown
## Authority
You MAY: <read anything>, <write the answer to X>, <close ticket N>, <commit to branch B>.
You MAY NOT, without asking: file/close/move tracker items beyond N · change where tracking
lives · mark any file superseded/read-only · write to any live external system · touch
branch <human's in-flight branch>.
If the ticket needs one of these, stop and report — that IS the deliverable.
```

Two lines of ceremony that would have prevented every finding in this review.

### 4. Add `agent-authored-commit-hygiene` to the map's `## Notes` skills list.

`map.md:46-47` names five skills; commit hygiene is not among them, and all three commits are
missing the `Co-authored-by` trailer as a result. Add it, and amend the trailer onto the three
commits — the branch is unmerged and unshared beyond origin, so this is cheap now and permanent
later.

### 5. Write down the live-mutation rule the 0005 session followed by instinct.

It got this right without guidance, which means the next one might not. Add to the wayfinder probe
clause (`SKILL.md:930-944`):

> A probe that **writes** to a live external system is a different risk class than one that reads.
> It requires: an explicit line in the brief or ticket authorising it; a fixture with zero live
> dependents (verify, do not assume — e.g. `projects: []`); deletion of every artefact created; and
> a verified post-state read proving no residue. Record all four in the findings asset.

That is nearly a transcript of what session 2 did (`0005-…-findings.md:118-123`), which is the
cheapest possible way to write a rule: promote the good instance.

---

## One thing worth saying plainly

Both sessions produced genuinely good work. Session 1 refuted the source report's only unverified
claim and correctly identified that api-version changes response *schema*, not just routes. Session
2 overturned session 1's own headline conclusion by running the one experiment that could settle it.
The autonomy that produced those results is the same autonomy that produced the banners. The fix is
a bounded authority statement, not less independence — a session that had to ask before every step
would not have found either result.
