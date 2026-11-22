---
command: plan apply
group: plans
summary: Apply a current Core plan under its exact confirmed digest and verify outcomes.
stability: stable
mutates: ado
---

# `twig plan apply`

```sh
twig plan apply --file change.json --confirm <exact-preview-digest> -o json
```

## Behavior

Requires an explicit canonical Core file and exact digest confirmation. Rechecks
connection, principal, schema, policy, observations and journal protections.
Matching retained draft intent uses the existing draft-save path; other intent
uses the same Core executor. No older plan schema or authorizer string supplies
authority.

Applying is durable before dispatch. Acknowledgement and independent verification
are separate. Only fully verified effects produce the successful
`planApplied` result. An already verified plan is not dispatched again.
Multiple operations are not one distributed transaction.

## Effects

Can publish provider mutations, persist attempts/receipts/mappings and settle
eligible exact drafts. It does not blindly flush unrelated work.

## Failure modes

Wrong digest, stale evidence, unavailable rules, denied access, conflict, failed,
partial or uncertain effects fail. Inspect the original
[`plan status`](plan-status.md) before considering any further operation;
never recreate an unknown seed or replay a verified prefix.
