# Issue tracker: GitHub

Issues, specs and implementation tickets live in `PolyphonyRequiem/twig` on
GitHub. GitHub is authoritative; do not create or maintain ADO mirrors. The legacy
ADO lifecycle, active-item, AB# commit and closeout instructions in
`.github/copilot-instructions.md` are obsolete.

## Client selection and safety

Prefer Twig to dogfood its supported GitHub operations against an already
initialized, explicitly verified GitHub connection to this repository. Check
installed help and capabilities; do not assume ADO commands, fields, states or
atomicity apply to GitHub. Use explicit targets, never implicit active-item
context for automation.

If Twig is unavailable, unconfigured or does not support the operation, use
built-in GitHub tools. Use `gh` when no suitable built-in operation is available.
Do not initialize a workspace, switch accounts or change bindings merely to
avoid this fallback.

Every limitation requiring a workaround, including client fallback or a
text-only substitute for a native relationship, is dogfooding feedback. Evaluate
whether it is a possible Twig bug, a missing capability, an intentional safety
boundary or an environment/setup problem. Report the limitation to the user with
the expected behavior, observed evidence, uncertainty and proposed workaround;
do not silently normalize it or label every refusal a bug. Surface suspected
bugs for the user's triage and filing decision without automatically creating
an issue.

Fallback is an alternative client, not a retry mechanism. After a possibly
dispatched Twig write, inspect its original proposal/journal and remote evidence
first. Never repeat an uncertain create, comment or update through another
client. Stale evidence, permission denials, policy refusals and conflicts must
be resolved, not bypassed by switching clients.

## Tracker operations

- Publish to the tracker: create a GitHub issue with context and acceptance criteria.
- Fetch a ticket: read its body, comments, labels, state, parent and blockers.
- Use native sub-issues for parent/child structure and native blocking dependencies
  for ordering. If unavailable, record explicit `Part of #N` and
  `Blocked by: #N` references in issue bodies.
- A ticket is startable only when all blockers are closed; do not treat rejection
  or closure alone as proof that a dependency's required deliverable exists.
- Claim a ticket by assigning it to the driving developer. Keep triage readiness
  separate from implementation progress.
- Record meaningful findings and completion evidence in comments. Close only when
  the agreed outcome is met; distinguish completed work from rejected requests.
  Do not close parents just because ticket generation finished.
- Reference GitHub issues in commits and PRs. Use closing keywords only when merge
  should close the issue; otherwise use non-closing references. No AB# requirement
  applies.

With `gh`, verify the target using `gh repo view --json nameWithOwner`; specify
`--repo PolyphonyRequiem/twig` for tracker operations. Use
`gh issue view/list/edit/comment/close`, `gh issue create`, and native
sub-issue/dependency commands supported by the installed version. Check help
rather than guessing flags. Follow the available built-in tools' contracts when
using them instead.

## Pull requests as a triage surface

**PRs as a request surface: no.**

## Wayfinding

A map is an issue labelled `wayfinder:map`; tickets are child issues labelled
`wayfinder:<type>`. The frontier consists of unassigned open children whose
blockers are satisfied. Claim before implementation, record the result on the
child, and add a result pointer to the map. Create any needed wayfinding labels
when that workflow is explicitly used.
