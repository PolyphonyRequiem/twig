# Bench rules and ADO planning

These retained native ADO routes operate on the shared user-wide Bench store.
They are not GitHub-qualified and do not restore old browser/configuration
envelopes. Use each exact command's `--help` for syntax.

```sh
twig bench rules MyWork -o json
twig bench planning show -o json
twig bench planning refresh -o json
twig bench sources -o json
```

## Behavior

`bench rules <name>` reads that Bench's selectors and captured revision without
provider reads. There is no implicit Bench. `bench rule add <name>` accepts qualified area/iteration/mine,
saved-query, or backlog intent. Conditions within one planning rule are AND;
independent pins/rules contribute by OR. Relative iterations require the actual
cached team calendar.

`bench rule remove <name> --index <one-based-index> --expect-revision <revision>`
requires a fresh listing; it never silently adopts a changed index.
Deletion changes the shared selector, not upstream work items or drafts.

`bench planning` and `bench planning show` read cached metadata.
`bench planning refresh` explicitly refreshes areas and the exact team's
iteration calendar without replacing Bench rules or fetching work items.

`bench sources` discovers saved-query hierarchy and exact-team backlogs without
executing them. `bench browse --query <guid>` or `--backlog <id>` explicitly
acquires membership/item bodies into cache; it never pins, selects, or publishes.

## Effects

Rule edits persist shared local intent under revision checks. Metadata refresh,
source discovery and browse can perform provider reads; none publishes work items.
Missing, ambiguous, stale or unsupported source/rule evidence refuses rather than
returning a fabricated empty selection.

## Failure modes

Inspect nonzero exits and complete output. A cached metadata read is not fresh
provider permission evidence. A successful browse is not approval to add a rule
or a claim that every provider supports this planning surface.

In the isolated v2 owner, `twig workspace --bench <name> -o json` inspects
released membership; add `--refresh` before `-o json` to acquire observations
for the selected Connection. Only `Known` has an exact total (including zero).
`Partial` lists qualified members without claiming the list is complete;
`Unknown` and `Denied` do not mean empty. Local edits never add provider members.
A relative iteration requires the selected team's calendar, observed no more
than 24 hours ago, with inclusive UTC dates and contiguous coverage through the
requested offset. An unusable calendar is not evidence of an empty iteration.
