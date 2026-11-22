# Plans and pending intent

| Command | Effect |
| --- | --- |
| [`pending`](pending.md) | Inspect typed drafts without changing them |
| [`plan validate`](plan-validate.md) | Parse current canonical Core format and digest |
| [`plan preview`](plan-preview.md) | Check application evidence and retain local journal intent |
| [`plan apply`](plan-apply.md) | Apply the named file under fresh native admission |
| [`plan status`](plan-status.md) | Inspect original operation statuses/receipts without replay |

All four plan verbs require an explicit file. The current Core format
replaces old plan v1/provider-plan v2 documents. Read the
[plan guide](../../features/plans.md) for identity, atomicity and recovery.

`plan latest/seed/reconcile`, `policy assess/reconcile`, and the captured `draft`
envelopes are retired. The native Bench editor assembles the same Core Plan
operations per connection without requiring an exported file.
