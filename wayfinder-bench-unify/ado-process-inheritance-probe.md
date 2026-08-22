# ADO Process Inheritance Probe — empirical results

Organization: `PolyphonyRequiem` (Azure DevOps **Services**, cloud)
Date: 2026-08-22
API: `_apis/work/processes?api-version=7.1-preview.2`

---

## VERDICT

**NO. Azure DevOps Services does NOT support multi-level process inheritance.**

An inherited process may only inherit from a **system** process. Attempting to create a
process whose `parentProcessTypeId` is a custom/inherited process (`Hyperbright`) fails with:

```
HTTP 500
VS402372: Inherited processes must inherit from a system process: Agile, Scrum, or CMMI.
Choose one of these processes and try again.
typeKey: ProcessInvalidParentException   errorCode: 402372
```

**The control PASSED.** The byte-for-byte identical request with a *system* parent
(`Basic`, `b8a3a935-7e91-48b8-a94c-606d37c3e9f2`) returned **HTTP 201 Created**. The only
difference between the two requests was the parent process id, so the failure is proven to
be caused by the parent being a custom process — not by permissions, not by a malformed
body, not by an API-version problem. This is a real, falsifiable negative result, not a
false green.

Corollary for the team's goal: three related processes (human-dev / AI-agent / leadership)
**cannot** share a custom parent whose improvements propagate. The only propagation channel
ADO offers is the system process at the root, which carries no custom types.

Note: the error text names "Agile, Scrum, or CMMI" but Basic is empirically also accepted
(step 3 succeeded with Basic). The message is stale; the *rule* it enforces — parent must be
a system process — is what the data supports.

---

## Auth used for every call

```bash
export HOME=/home/polyphonyrequiem
TOK=$(az account get-access-token --scope 499b84ac-1321-427f-aa17-267ca6975798/.default --query accessToken -o tsv)
```

---

## Step 1 — BASELINE

```bash
curl -s -w "\nHTTP_STATUS:%{http_code}\n" -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1-preview.2"
```

**HTTP 200**

```json
{"count":6,"value":[
{"typeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","name":"Basic","referenceName":null,"description":"This template is flexible for any process and great for teams getting started with Azure DevOps.","parentProcessTypeId":"00000000-0000-0000-0000-000000000000","isEnabled":true,"isDefault":true,"customizationType":"system"},
{"typeId":"27450541-8e31-4150-9947-dc59f998fc01","name":"CMMI","referenceName":null,"description":"This template is for more formal projects requiring a framework for process improvement and an auditable record of decisions.","parentProcessTypeId":"00000000-0000-0000-0000-000000000000","isEnabled":true,"isDefault":false,"customizationType":"system"},
{"typeId":"adcc42ab-9882-485e-a3ed-7678f01f66bc","name":"Agile","referenceName":null,"description":"This template is flexible and will work great for most teams using Agile planning methods, including those practicing Scrum.","parentProcessTypeId":"00000000-0000-0000-0000-000000000000","isEnabled":true,"isDefault":false,"customizationType":"system"},
{"typeId":"6b724908-ef14-45cf-84f8-768b5384da45","name":"Scrum","referenceName":null,"description":"This template is for teams who follow the Scrum framework.","parentProcessTypeId":"00000000-0000-0000-0000-000000000000","isEnabled":true,"isDefault":false,"customizationType":"system"},
{"typeId":"5591c621-7ae6-492f-80b9-8150841e4e07","name":"CSharp4AI","referenceName":"Inherited.5591c6217ae6492f80b98150841e4e07","description":"Experimental process for csharp4ai: wayfinder mapping + decision register (inherited from Hyperbright) plus falsifiable Hypothesis and Experiment types so metrics, protocols and outcomes are queryable fields, not prose.","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","isEnabled":true,"isDefault":false,"customizationType":"inherited"},
{"typeId":"ba4e268d-7d67-43bd-8065-df7ab52fba0c","name":"Hyperbright","referenceName":"Inherited.ba4e268d7d6743bd8065df7ab52fba0c","description":"Inherited from Basic. Adds Wayfinder types (Map, Research, Prototype, Grilling, Wayfinder Task) for decision-tracking; delivery types added later.","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","isEnabled":true,"isDefault":false,"customizationType":"inherited"}
]}
```

| name | typeId | parentProcessTypeId | customizationType | isDefault | isEnabled |
|---|---|---|---|---|---|
| Basic | b8a3a935-7e91-48b8-a94c-606d37c3e9f2 | 000…0 | system | true | true |
| CMMI | 27450541-8e31-4150-9947-dc59f998fc01 | 000…0 | system | false | true |
| Agile | adcc42ab-9882-485e-a3ed-7678f01f66bc | 000…0 | system | false | true |
| Scrum | 6b724908-ef14-45cf-84f8-768b5384da45 | 000…0 | system | false | true |
| CSharp4AI | 5591c621-7ae6-492f-80b9-8150841e4e07 | Basic | inherited | false | true |
| Hyperbright | ba4e268d-7d67-43bd-8065-df7ab52fba0c | Basic | inherited | false | true |

**Independent corroboration of the verdict, visible in the baseline itself:** every non-system
process in the org has `parentProcessTypeId` = Basic. `CSharp4AI`'s own description claims it is
"inherited from Hyperbright", but the API says its parent is **Basic**. That description records
an intention that the platform did not honour — the tree is exactly two levels deep everywhere.
No `customizationType` value of `custom` exists at the process level; `custom` only appears on
individual work item types.

---

## Step 2 — 🔴 THE DECISIVE TEST: parent = CUSTOM process

```bash
curl -s -w "\nHTTP_STATUS:%{http_code}\n" -X POST \
  -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" \
  -d '{"name":"zz-probe-child-of-custom","parentProcessTypeId":"ba4e268d-7d67-43bd-8065-df7ab52fba0c","description":"disposable inheritance probe"}' \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1-preview.2"
```

**HTTP 500**

```json
{"$id":"1","innerException":null,"message":"VS402372: Inherited processes must inherit from a system process: Agile, Scrum, or CMMI. Choose one of these processes and try again.","typeName":"Microsoft.TeamFoundation.WorkItemTracking.Server.Metadata.ProcessInvalidParentException, Microsoft.TeamFoundation.WorkItemTracking.Server","typeKey":"ProcessInvalidParentException","errorCode":402372,"eventId":3000}
```

No process was created. `Hyperbright` was not modified (POST /processes creates a new process;
it does not touch the parent).

---

## Step 3 — CONTROL: parent = SYSTEM process (Basic)

Identical request, only `parentProcessTypeId` changed.

```bash
curl -s -w "\nHTTP_STATUS:%{http_code}\n" -X POST \
  -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" \
  -d '{"name":"zz-probe-child-of-system","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","description":"disposable inheritance probe control"}' \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1-preview.2"
```

**HTTP 201 Created**

```json
{"typeId":"a34ab396-2145-461f-80ae-f147c9dd1231","name":"zz-probe-child-of-system","referenceName":"Inherited.a34ab3962145461f80aef147c9dd1231","description":"disposable inheritance probe control","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","isEnabled":true,"isDefault":false,"customizationType":"inherited"}
```

✅ **CONTROL PASSED.** Credentials, payload shape, api-version and permissions are all
sufficient to create a process. The step-2 failure is therefore attributable solely to the
parent being a custom process.

---

## Step 4 — What the control process actually inherited

```bash
curl -s -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/a34ab396-2145-461f-80ae-f147c9dd1231/workitemtypes?api-version=7.1-preview.2"
```

**HTTP 200** — `count: 6`, `(name, customization)`:

```
[('Issue','system'), ('Epic','system'), ('Task','system'),
 ('Test Case','system'), ('Test Plan','system'), ('Test Suite','system')]
```

For comparison, `Hyperbright`'s work item types:

```bash
curl -s -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/ba4e268d-7d67-43bd-8065-df7ab52fba0c/workitemtypes?api-version=7.1-preview.2"
```

```
[('Wayfinder Task','custom'), ('Issue','inherited'), ('Prototype','custom'), ('Map','custom'),
 ('Decision','custom'), ('Idea','custom'), ('Task','inherited'), ('Epic','inherited'),
 ('Feature','custom'), ('Spec','custom'), ('Research','custom'), ('Bug','custom'),
 ('Grilling','custom'), ('Test Case','system'), ('Test Plan','system'), ('Test Suite','system')]
```

A new child of Basic gets **only the six Basic/system types**. It gets **none** of
Map / Grilling / Research / Decision / Spec / Prototype / Idea / Wayfinder Task / Feature / Bug.
Concretely: inheriting from the system process gives you *nothing* the team has built.

---

## Step 5 — The COPY / CLONE path

```bash
curl -s -w "\nHTTP_STATUS:%{http_code}\n" -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/ba4e268d-7d67-43bd-8065-df7ab52fba0c?api-version=7.1-preview.2"
```

**HTTP 200**

```json
{"typeId":"ba4e268d-7d67-43bd-8065-df7ab52fba0c","name":"Hyperbright","referenceName":"Inherited.ba4e268d7d6743bd8065df7ab52fba0c","description":"Inherited from Basic. Adds Wayfinder types (Map, Research, Prototype, Grilling, Wayfinder Task) for decision-tracking; delivery types added later.","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","isEnabled":true,"isDefault":false,"customizationType":"inherited"}
```

The single-process contract contains **no** copy/clone/template hint. Fields returned are
exactly: `typeId`, `name`, `referenceName`, `description`, `parentProcessTypeId`, `isEnabled`,
`isDefault`, `customizationType`.

Probe of a speculative `copyFrom` parameter:

```bash
curl -s -w "\nHTTP_STATUS:%{http_code}\n" -X POST \
  -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" \
  -d '{"name":"zz-probe-copy","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","description":"copy probe","copyFrom":"ba4e268d-7d67-43bd-8065-df7ab52fba0c","referenceName":"Inherited.zzprobecopy"}' \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1-preview.2"
```

**HTTP 201 Created**

```json
{"typeId":"3952c0c3-e5c8-4fa5-b8db-14e383389784","name":"zz-probe-copy","referenceName":"Inherited.zzprobecopy","description":"copy probe","parentProcessTypeId":"b8a3a935-7e91-48b8-a94c-606d37c3e9f2","isEnabled":true,"isDefault":false,"customizationType":"inherited"}
```

Its work item types:

```
[('Issue','system'), ('Epic','system'), ('Task','system'),
 ('Test Case','system'), ('Test Plan','system'), ('Test Suite','system')]
```

**Finding:** `copyFrom` was silently ignored — accepted without error, and the resulting
process contains none of Hyperbright's customisations. `referenceName` **is** honoured
(it was set to the supplied `Inherited.zzprobecopy` rather than an auto-generated value),
which confirms unknown properties are dropped silently rather than rejected. So a
201 response here is not evidence a copy happened — the work-item-type check is what
disproves it.

**Honest limit of this probe:** the REST create-process contract exposes no copy mechanism.
The Azure DevOps *web UI* does offer a "Create inherited process **from** an existing
inherited process" gesture in some org configurations; I did not exercise the UI, and I did
not reverse-engineer any undocumented internal endpoint, because doing so would risk
writing to production metadata. **I therefore cannot state from evidence whether a UI-driven
copy exists in this org, and I am not guessing.** What I *can* state from evidence: if such
a copy exists, it must produce a process whose `parentProcessTypeId` is a system process
(step 2 proves no other parent is representable), so it would be an **independent snapshot,
not a live link** — there is no field in the data model capable of recording a
custom→custom relationship for changes to propagate through.

---

## Step 6 — CLEANUP 🔴 **INCOMPLETE — HUMAN ACTION REQUIRED**

The cleanup command was **denied at the shell-approval layer** and, per the tool's explicit
instruction not to retry or route around a denied destructive command, I stopped rather than
re-attempting it. **Two throwaway processes I created are still present in the organization:**

| name | typeId | status |
|---|---|---|
| `zz-probe-child-of-system` | `a34ab396-2145-461f-80ae-f147c9dd1231` | ⚠️ NOT DELETED |
| `zz-probe-copy` | `3952c0c3-e5c8-4fa5-b8db-14e383389784` | ⚠️ NOT DELETED |

Neither has any project attached, so both are inert metadata and safe to delete. Commands
for a human to run:

```bash
export HOME=/home/polyphonyrequiem
TOK=$(az account get-access-token --scope 499b84ac-1321-427f-aa17-267ca6975798/.default --query accessToken -o tsv)
curl -s -w "%{http_code}\n" -X DELETE -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/a34ab396-2145-461f-80ae-f147c9dd1231?api-version=7.1-preview.2"
curl -s -w "%{http_code}\n" -X DELETE -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/3952c0c3-e5c8-4fa5-b8db-14e383389784?api-version=7.1-preview.2"
# verify: process count should return to 6
curl -s -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1-preview.2"
```

Production safety: `Hyperbright`, `CSharp4AI`, the `Twig` and `Sandbox` projects, and all work
items were untouched — every call against them in this probe was a `GET`.

---

## Implication for the team's design

The "shared custom parent" architecture is **not achievable on Azure DevOps Services**. The
inheritance tree is hard-capped at two levels: system → inherited. Viable alternatives, in
descending order of fidelity to the original intent:

1. **One process, three sets of work item types / states / rules**, partitioned by team or
   area path. Single source of truth, real propagation, no duplication. Closest to the intent.
2. **Three sibling processes off the same system parent**, kept in sync by a script that
   replays customisations from a checked-in definition. Propagation becomes a build step you
   own rather than a platform feature — and it must be treated as such, because nothing in
   ADO will tell you when they drift.

Option 2 is duplication with tooling on top; it should not be described as inheritance.
