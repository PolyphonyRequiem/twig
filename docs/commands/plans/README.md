# Proposals and pending intent

| Command | Effect |
| --- | --- |
| [`pending`](pending.md) | Inspect typed drafts without changing them |
| [`proposal validate`](proposal-validate.md) | Parse current canonical Core format and digest |
| [`proposal preview`](proposal-preview.md) | Check application evidence and retain local journal intent |
| [`proposal apply`](proposal-apply.md) | Apply under the exact confirmed digest and fresh native admission |
| [`proposal status`](proposal-status.md) | Inspect original operation statuses/receipts without replay |

## Canonical vs legacy verbs

`plan validate`, `plan preview`, `plan apply`, and `plan status` are deprecated
aliases of the same four handlers. Prefer `proposal`; the alias does not accept
an older JSON schema.

All four proposal verbs require an explicit file. The current Core format
replaces old plan v1/provider-plan v2 documents. Read the
[proposal guide](../../features/proposals.md) for identity, atomicity and recovery.

`proposal latest/seed/reconcile`, `plan seed`, `policy assess/reconcile`, and the
captured `draft` envelopes are retired. The daemon's native Plan/editor is a
separate guarded API, not an alternate CLI envelope publisher.
