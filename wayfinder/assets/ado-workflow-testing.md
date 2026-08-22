# Testing ADO work item workflows — what the documentation actually says

**Status:** evidence memo. Primary sources only (Microsoft Learn REST API reference,
`MicrosoftDocs/azure-devops-docs`, `az boards` CLI reference). Every claim below is quoted or
paraphrased from a cited page. Where the documentation is silent, this file says so rather than
filling the gap.

**Provenance:** the reading was done by a background research agent (46 calls, ~28 min) which
exhausted its iteration budget *before* writing its findings out. The cached primary sources it
fetched survived in `/tmp/adodoc/` and `/tmp/adodocs/`, and this file was assembled from those.
🔴 That failure is itself the false-green shape: the delegation reported `status=completed`
while producing **no artifact**. A "completed" status is not a deliverable — check for the file.

⚠️ **Scope limit.** This memo answers what the *ADO platform* documents. It does not describe
`twig`'s behaviour, which sits a layer above and keeps its own SQLite mirror. For measured
`twig`-level behaviour see the `testing-ado-workflows` skill. Where the two disagree, both facts
matter: the platform's rule, and what the CLI actually does with it.

---

## What a test harness MUST do

1. **Never pass `destroy=true`.** Ordinary delete is reversible via the Recycle Bin; destroy is
   documented as permanent with "no way to restore/recover". (§1)
2. **Do not assume `$batch` is atomic — the docs contradict themselves on this point.** Treat a
   partial batch outcome as possible and verify each item. (§4)
3. **Honour `Retry-After`.** Critically, a throttled response still returns **HTTP 200**, so a
   naive harness sees success and never retries. (§3)
4. **Do not assume a state transition is refused.** Rules can be bypassed, and `twig` was
   measured letting a Task skip a state. Enumerate valid states; don't assert on refusal. (§5)
5. **Verify by re-reading the item, not by trusting the write's response.**
6. **Budget for a 10,000-revision ceiling per work item** on API updates. A long-lived test
   fixture mutated in a loop will eventually hit it. (§4)

---

## 1. Deletion, the Recycle Bin, and destroy

**Source:** [Work Items - Delete, REST 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/delete?view=azure-devops-rest-7.1)

Quoted verbatim from the reference:

> Deletes the specified work item and sends it to the Recycle Bin, so that it can be restored
> back, if required. Optionally, if the destroy parameter has been set to true, it destroys the
> work item permanently. **WARNING: If the destroy parameter is set to true, work items deleted
> by this command will NOT go to recycle-bin and there is no way to restore/recover them after
> deletion. It is recommended NOT to use this parameter.** If you do, please use this parameter
> with extreme caution.

And on the parameter itself:

> `destroy` — Optional parameter, if set to true, the work item is deleted permanently. Please
> note: the destroy action is PERMANENT and cannot be undone.

So there are **three** distinct dispositions, and they are not interchangeable:

| Disposition | What it is | Reversible |
| --- | --- | --- |
| `Removed` **state** | an ordinary field value; the item still exists and is still queryable | yes — set the state back |
| **Delete** (default) | `DELETE .../workitems/{id}` — moves it to the Recycle Bin | yes — Recycle Bin restore |
| **Destroy** | `DELETE .../workitems/{id}?destroy=true` | 🔴 **no** |

The Recycle Bin has its own endpoint family
([Recycle Bin, REST 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/recyclebin?view=azure-devops-rest-7.1))
for listing, restoring and destroying items already in it.

**Recommended cleanup:** ordinary delete (no `destroy`), or better, leave the items and dispose
by state. A recoverable mistake beats an unrecoverable tidy-up.

**Not documented; verify empirically:** whether deleted work item **IDs are ever reused**. No
first-party statement was found either way. Do not build a test that depends on ID reuse or on
its absence.

---

## 2. Read-after-write consistency

🔴 **The premise of the original question was not confirmed.** The research brief asserted that
Microsoft documents a delay between a work item write and its appearance in WIQL results, and
asked for the quote. **No such statement was found** in the REST reference, the WIQL syntax
page, the query FAQs, or a repo-wide grep of `MicrosoftDocs/azure-devops-docs` for
`immediately`, `queryable`, `delay`, `index`, `stale`, and `asynchronous`.

Recording that as a negative result rather than manufacturing a citation. **Not documented;
verify empirically.**

**A near-miss worth naming, because it is easy to cite by mistake.** The
[Analytics / Power BI documentation](https://learn.microsoft.com/en-us/azure/devops/report/powerbi/reporting-roadmap)
states:

> **Analytics** isn't a real-time store but a curated copy of data with up to a 30-second delay
> before changes appear.

⚠️ **That is the Analytics store, which is a different system from WIQL.** WIQL queries the
work item store directly. Do not cite the 30-second figure as a WIQL latency — it is a documented
fact about a different subsystem, and using it here would be inference wearing a citation's
clothes.

**Measured against `PolyphonyRequiem/Sandbox`** (not a documentation claim): create-then-query
by title returned `count: 1` immediately, and again after 5 s. No lag observed on that path.

**Practical guidance:** prefer reading the **item by ID** over querying for it. A GET by ID is
the direct read; a query is an index lookup and is the thing whose freshness is undocumented.

---

## 3. Rate limits and throttling

**Source:** [Rate and usage limits](https://learn.microsoft.com/en-us/azure/devops/integrate/concepts/rate-limits)

The model is **TSTUs** — Azure DevOps throughput units:

> One TSTU represents the average load generated by a typical Azure DevOps user over five
> minutes.

> The global limit is **200 TSTUs within any sliding five-minute window.**

> Normal user activity can generate spikes of 10 TSTUs or fewer per five minutes. Larger but
> less frequent spikes can reach up to 100 TSTUs.

TSTUs are deliberately not computable client-side:

> You can't calculate usage in TSTUs for an action with a formula, but you can see how many
> TSTUs an operation consumes on the usage monitoring page. Some operations, **like work item
> queries, vary in consumption as your organization grows** …

What throttling looks like:

> The delay depends on the user's sustained level of consumption. **Delays range from a few
> milliseconds per request up to 30 seconds.** When consumption drops to zero or the resource
> isn't overwhelmed, the delays stop within five minutes. If consumption stays high, delays can
> continue indefinitely to protect the resource.

🔴 **The false-green trap, stated by Microsoft itself:**

> Honor the `Retry-After` header: If you receive it in a response, wait the specified time
> before sending another request. **The response still returns HTTP 200**, so retry logic isn't
> required.

A harness that checks only the status code sees `200` and concludes all is well, while being
actively throttled and silently slowed. **Check for `Retry-After` on success responses**, not
only on errors.

> Monitor `X-RateLimit` headers: If available, track `X-RateLimit-Remaining` and
> `X-RateLimit-Limit` to approximate how quickly you're approaching the threshold.

Note the hedge — "**If available**". Do not require these headers to be present.

---

## 4. Batch, atomicity, idempotency

**Source:** [Work Items - Update (batch), REST](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update)

🔴 **The documentation contradicts itself, and this must not be resolved by picking the
convenient half.** The same reference page says both:

> Performs multiple Work Item Update requests. Response contains individual responses for each
> of the requests in the batch. **Failed requests do not affect subsequent requests in the
> batch.**

and, in its worked example headed *"Case where single request in batch api fails"*:

> **If a single request fails then the whole batch api will get failed.**

with the sample response body:

> `{"count":1,"value":{"Message":"TF401321: Whole Bulk failed."}}`

The repo's prior research concluded `$batch` is **not atomic**. That conclusion is
**consistent with the first sentence but contradicted by the second**, so this memo does not
declare a winner. **Treat both outcomes as possible and verify per-item after any batch.** The
sample responses also show per-item HTTP codes (400, 500) alongside successes, so a caller must
read the individual entries regardless.

**Revision ceiling** — [Work tracking limits](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits):

> The REST API for Azure DevOps Services enforces a work item **revision limit of 10,000
> updates**. This limit applies only to REST API updates and doesn't affect updates made through
> the web portal.

**Idempotency:** no idempotency key for creates was found in the reference. **Not documented;
assume none.** A client must handle duplicate-create-on-retry itself.

**But there IS an optimistic-concurrency guard for updates**, which the reference's own examples
use — a JSON-Patch test operation against the revision:

```json
{ "op": "test", "path": "/rev", "value": 3 }
```

Include it to make an update conditional on the revision you read. Without it, a retry can
clobber a concurrent change.

`bypassRules`, `validateOnly` and `suppressNotifications` are query parameters on the update
endpoint — `validateOnly=true` is useful in a harness for checking a payload without writing.

---

## 5. State transitions

**Source:** [Work Item Transitions - List, REST 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-transitions/list?view=azure-devops-rest-7.1)

There **is** a documented way to enumerate the next legal transition:

```
GET https://dev.azure.com/{organization}/_apis/wit/workitemtransitions?ids={ids}&api-version=7.1
```

It returns a `stateOnTransition` per item, and documents an error code and message "if there is
no next state transition possible".

The per-type state list and its full transition map are available from
[Work Item Types - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/get?view=azure-devops-rest-7.1),
whose payload includes a `transitions` object mapping each state to its allowed destinations.

🔴 **Rules are bypassable, so "the process will stop me" is not a safe assumption.**
[Rule reference](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference):

> In general, all work items are validated by the rule engine when users modify the work item.
> However, to support certain scenarios, users assigned the **Bypass rules on work item updates**
> project-level permission can save work items without rules being evaluated.

**Measured against Sandbox** (a `twig`-level observation, not a platform claim): a Task moved
`To Do → Done`, skipping `Doing`, exit 0, confirmed by re-read. Whatever the mechanism, **do not
write a test asserting an illegal transition is refused.**

---

## 6. Test isolation

**Not documented.** No first-party guidance was found recommending a separate project for
automated testing, nor any documented convention for tagging, area-path scoping, or run-id
naming to isolate test data. Searches across the docs repo returned nothing on point.

This is a gap in the documentation, not a gap in the practice — the pattern this repo uses
(dedicated `Sandbox` project, run-id tag, free-text lookup by that tag) is a **local convention
justified on its own merits**, not something Microsoft prescribes. It should be described as
such and not dressed in a citation.

Relevant documented facts that bear on isolation:

- `az boards query` "**Only supports flat queries**"
  ([az boards query](https://learn.microsoft.com/en-us/cli/azure/boards?view=azure-cli-latest)) —
  so hierarchical assertions need the REST API or a different tool.
- Work item **tags** are a first-class field (`System.Tags`) and are queryable via WIQL
  `CONTAINS`.

---

## Open questions

Listed so nobody closes them with inference:

- **WIQL read-after-write latency** — undocumented. Measured as immediate on one path in
  Sandbox; not a guarantee.
- **Work item ID reuse after deletion** — undocumented.
- **`$batch` atomicity** — the reference contradicts itself; unresolved.
- **Create idempotency** — no key documented; assume none.
- **Whether `X-RateLimit-*` headers are present** on work item endpoints — the docs hedge with
  "if available".
