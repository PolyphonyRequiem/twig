# What an ADO inherited process can and cannot express

Memo for wayfinder ticket **0009**. Target: process `ba4e268d-7d67-43bd-8065-df7ab52fba0c`
("Hyperbright", inherited from Basic), org `https://dev.azure.com/PolyphonyRequiem`.
All mutations were run against **PolyphonyRequiem/Sandbox**, never `Twig`. Every probe below
carries its verifying GET; probes without one are labelled **UNVERIFIED**.

Every claim is tagged **[doc]** (learn.microsoft.com) or **[probe]** (live, with the request).

Common prefix used below: `B = https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/ba4e268d-7d67-43bd-8065-df7ab52fba0c`

## 0. Prior research — cited, not redone

From `/home/polyphonyrequiem/repos/twig-bench-unify/wayfinder-bench-unify/`:

- `ado-parent-child-enforcement.md` — **ADO cannot enforce type-level parent/child policy at
  all** (six avenues closed). Nothing in this memo changes that: no rule condition or action
  found here can reference a link or a parent's type.
- `ado-backlog-levels.md` — **backlog level governs display, not link legality.**
- `ado-process-inheritance.md` / `-probe.md` — **multi-level process inheritance does not
  exist** (custom parent → `HTTP 500 VS402372`; identical request with system parent → `201`).
- `ado-audience-views.md`.

This memo independently re-confirms the *type-level* analogue of the inheritance finding — see
§1.3, `inheritsFrom` a custom type is refused.

---

## 1. Creating a work item type

### 1.1 A type CAN be created from scratch, with no parent and no backlog level — **[probe]**

```
POST B/workitemtypes?api-version=7.1-preview.2
{"name":"Probe0009Scratch","description":"probe ticket 0009",
 "color":"009CCC","icon":"icon_book","isDisabled":false}
→ 201 Created
{"referenceName":"Hyperbright.Probe0009Scratch", ... "inherits":null}
```
Verifying GET `B/workitemtypes/Hyperbright.Probe0009Scratch?api-version=7.1-preview.2` → `200`,
same body, `"inherits": null`, `"customization": "custom"`.

Verifying GET `B/workitemtypesbehaviors/Hyperbright.Probe0009Scratch/behaviors?api-version=7.1-preview.1`
→ `200 {"count":0,"value":[]}`. **A type with no backlog level is legal and is the default.**
Confirmed **[doc]**: "By default, custom work item types aren't added to any backlog."
(https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-work-item-type?view=azure-devops)

### 1.2 Required inputs and what the server validates — **[probe]**

`name` + `color` + `icon` are the meaningful inputs; `description` and `isDisabled` optional.
The server validates colour and icon strictly:

```
POST B/workitemtypes  {"name":"Probe0009Scratch3","icon":"icon_not_a_real_icon", ...}
→ 400  VS403344: You've specified an invalid icon Id 'icon_not_a_real_icon'.

POST B/workitemtypes  {"name":"Probe0009Scratch4","color":"notacolor", ...}
→ 400  VS403064: You've specified an invalid color 'notacolor'.
        Color should be a 6 character hexadecimal value.
```
Colour is a bare 6-char hex (no `#`). Icon is from a fixed server-side enum (`icon_book`,
`icon_crown`, `icon_insect`, `icon_gavel`, `icon_test_case`, … — the live process uses 13 distinct
ones). Neither failure created anything: the verifying GET of the process type list after the
whole probe run returns the original **16 types**, unchanged.

### 1.3 `inheritsFrom` may name a SYSTEM type only — a custom type is refused — **[probe]**

```
POST B/workitemtypes {"name":"Probe0009Scratch2","inheritsFrom":"Hyperbright.Spec", ...}
→ 404  VS402805: Cannot find work item type with reference name 'Hyperbright.Spec'
       in process named 'b8a3a935-7e91-48b8-a94c-606d37c3e9f2'.
```
Note the process GUID in the error is **not** the Hyperbright process — the server resolves
`inheritsFrom` against the *parent system process*, so a custom sibling is invisible by
construction. This is the type-level mirror of the settled process-level finding: **there is no
second level of inheritance anywhere in this model.**

### 1.4 What `inherits` fixes forever — **[probe, partial]**

`inherits` is set at creation and is not present as a writable property on
`PATCH B/workitemtypes/{rn}` (which accepts `name`, `description`, `color`, `icon`,
`isDisabled` — the disable PATCH in §3.1 succeeded and returned the full object with `inherits`
unchanged). I did **not** attempt a PATCH that tries to change `inherits` on an existing type, so
**"inherits can never be changed" is OPEN as a probed claim** — it is a documented design
statement, not something I measured here.

What inheritance *does* fix, measured: an inherited type's states carry
`"customizationType":"system"` and cannot be deleted (see §6.2), whereas a from-scratch type's
own three default states are all `"customizationType":"custom"`.

---

## 2. Assigning a type to a backlog level

### 2.1 The call is POST (create) / PUT (update) on `workitemtypesbehaviors` — PATCH fails — **[probe]**

The endpoint is **not** `.../workItemTypes/{rn}/behaviors` — that 404s with an ASP.NET
"controller not found" HTML page (a misleading 404: the resource exists under a different path).
The real path is:

```
GET  B/workitemtypesbehaviors/{referenceName}/behaviors?api-version=7.1-preview.1
POST B/workitemtypesbehaviors/{referenceName}/behaviors?api-version=7.1-preview.1
     {"behavior":{"id":"System.RequirementBacklogBehavior"},"isDefault":false}
```

Measured:
```
POST … → 200 {"behavior":{"id":"System.RequirementBacklogBehavior"},"isDefault":false, ...}
```
Verifying GET `B/workitemtypesbehaviors/Hyperbright.Probe0009Scratch/behaviors` →
`200 {"count":1, value:[{behavior:{id:"System.RequirementBacklogBehavior"}}]}`.

PATCH against a behaviour the type does not yet reference:
```
PATCH B/workitemtypesbehaviors/Hyperbright.Probe0009Scratch/behaviors
→ 500  The Hyperbright.Probe0009Scratch work item type does not reference the
       System.RequirementBacklogBehavior behavior.
```
So **PATCH is an update-in-place of an existing association only** (chiefly to flip `isDefault`);
adding an association requires POST. The ticket's warning that "behaviour edits are PUT, not
PATCH; PATCH returns 405" is *directionally right but not literally what I saw here*: on the
`workitemtypesbehaviors` route PATCH returns **500 BehaviorReferenceDoesNotExistsException**, not
405. The 405 in the ticket presumably came from the process-level `B/behaviors/{id}` route.
I did not re-derive the 405; treat that as inherited context.

### 2.2 A type CANNOT sit at two backlog levels — hard server refusal — **[probe]**

```
POST B/workitemtypesbehaviors/Hyperbright.Probe0009Scratch/behaviors
     {"behavior":{"id":"System.TaskBacklogBehavior"},"isDefault":false}
→ 400  VS403194: The Hyperbright.Probe0009Scratch work item type already references a
       behavior. Adding multiple behavior references to a work item type is currently
       not supported.
```
Verifying GET afterwards → still `count:1`, still `System.RequirementBacklogBehavior`. **One
backlog level per type, maximum. This is a refusal, not a UI limitation.** (The wording
"currently not supported" is Microsoft's; do not design around it changing.)

### 2.3 A backlog level CAN be removed later — **[probe]**

```
DELETE B/workitemtypesbehaviors/Hyperbright.Probe0009Scratch/behaviors/System.RequirementBacklogBehavior
→ 204
```
Verifying GET → `200 {"count":0,"value":[]}`. The type survives; only the association is gone.
So level assignment is fully reversible: add, remove, add a different one.

### 2.4 Live level map (context) — **[probe]**

`GET B/behaviors?api-version=7.1-preview.2`:
`Microsoft.VSTS.Basic.EpicBacklogBehavior` (Initiatives, rank 30, inherited) →
`System.RequirementBacklogBehavior` (Work, rank 20, inherited) →
`System.TaskBacklogBehavior` (Tasks, rank 10, system); plus `System.OrderedBehavior` and
`System.PortfolioBacklogBehavior` at rank 0. Behaviour *reference names* never change when the
display name is renamed — `Initiatives` is still `…EpicBacklogBehavior`.

---

## 3. Renaming, hiding and removing types — **the ticket-0005 input**

### 3.1 A custom type can be disabled, and disabling DOES block REST writes — **[probe]**

```
PATCH B/workitemtypes/Hyperbright.Probe0009Scratch?api-version=7.1-preview.2  {"isDisabled":true}
→ 200
```
Verifying GET `B/workitemtypes/Hyperbright.Probe0009Scratch` → `200 … "isDisabled": true`.

Then a REST create against it:
```
POST https://dev.azure.com/PolyphonyRequiem/Sandbox/_apis/wit/workitems/$Probe0009Scratch?api-version=7.1
     [{"op":"add","path":"/fields/System.Title","value":"probe0009 disabled write"}]
→ 500  VS403074: Work item creation or migration to the target work item type
       'Probe0009Scratch' is blocked. Enable the work item type to unblock the operations.
```
**Disabling is a server-side block, not a UI hide.** Confirmed **[doc]**: "Disabling a WIT
removes the WIT from the New dropdown menu and add experiences. It also blocks creating a work
item of that WIT type through REST APIs."
(https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-work-item-type?view=azure-devops)

### 3.2 `bypassRules=true` does NOT defeat a disabled type — **[probe]** ⚠️ important

```
POST …/Sandbox/_apis/wit/workitems/$Probe0009Scratch?bypassRules=true&api-version=7.1
→ 500  VS403074 … is blocked. Enable the work item type to unblock the operations.
```
Same refusal. Type-disabling sits *above* the rule engine. This is the only enforcement
mechanism found in this memo that `bypassRules` cannot walk through — contrast §5.2.

The same holds for the already-disabled inherited type `Hyperbright.Issue`
(`"isDisabled": true` in the live process): `POST …/workitems/$Issue` → `500 VS403074`.
Existing items of a disabled type remain fully editable — `PATCH` of `System.Title` on Issue
#137 returned `200 rev 15`, verifying GET showed the new title, and the original was restored
(`rev 16`). Confirmed **[doc]**: "No changes are made to existing work items of that type. You
can update or delete them, and they continue to appear on backlogs and boards."

### 3.3 A custom type CAN be deleted outright — **[probe]**

```
DELETE B/workitemtypes/Hyperbright.Probe0009Scratch?api-version=7.1-preview.2  → 204
```
Verifying GET → `500 VS1640142: Work item type not found or you do not have permission in the
process …`. Gone. Its rules and its custom state went with it.
**[doc]** warns destroying a WIT deletes all work items and historical data for that type
(same page, "Delete or destroy a custom WIT"). My scratch type had zero items.

### 3.4 An inherited type with live data CANNOT be deleted — **[probe]**

```
DELETE B/workitemtypes/Hyperbright.Issue?api-version=7.1-preview.2
→ 500  Can't delete work item type Issue. There are active work items associated to it.
       (ActiveWorkItemsExistException)
```
Verifying GET → still present, `isDisabled: true`, description "RETIRED 2026-08-15…".
The refusal is about **active work items**, not about `inherited` per se. I could not separate
the two: I have no inherited type with zero items to test against, so **"an inherited type with
zero items can/cannot be deleted" is OPEN.** What is settled is the *practical* answer for
Hyperbright: disable, don't delete.

### 3.5 🔴 The four dormant types are NOT in the process at all — they cannot be hidden or removed — **[probe]**

This is the direct ticket-0005 input, and the answer is not the expected one.

`GET B/workitemtypes?$expand=all&api-version=7.1-preview.2` returns **16** types. The set is
exactly:
`Hyperbright.{Bug,Decision,Epic,Feature,Grilling,Idea,Issue,Map,Prototype,Research,Spec,Task,WayfinderTask}`
+ `Microsoft.VSTS.WorkItemTypes.{TestCase,TestPlan,TestSuite}`.

**Code Review Request/Response and Feedback Request/Response are absent from the process.**
Confirming that this is a genuine absence and not a listing quirk:
```
GET  B/workitemtypes/Microsoft.VSTS.WorkItemTypes.CodeReviewRequest  → 500 VS1640142 (not found)
GET  B/workitemtypes/Microsoft.VSTS.WorkItemTypes.FeedbackRequest    → 500 VS1640142 (not found)
GET  B/workitemtypes/Microsoft.VSTS.WorkItemTypes.SharedStep         → 500 VS1640142 (not found)
PATCH B/workitemtypes/Microsoft.VSTS.WorkItemTypes.CodeReviewRequest {"isDisabled":true}
                                                                     → 404 VS402805 (not found)
```
They exist only at **project** scope. `GET …/Sandbox/_apis/wit/workitemtypes?api-version=7.1`
returns **22** types — the 16 above plus Shared Steps, Shared Parameter, Code Review
Request/Response, Feedback Request/Response.

They are already hidden the only way ADO hides them — via the category:
```
GET …/Sandbox/_apis/wit/workitemtypecategories?api-version=7.1
Microsoft.HiddenCategory | Hidden Types Category ->
  ['Issue','Code Review Request','Code Review Response','Shared Steps','Shared Parameter',
   'Test Suite','Test Plan','Test Case','Feedback Response','Feedback Request']
```

**Does hidden-category membership stop a REST write? No — it is purely a UI hide.** — **[probe]**
```
POST …/Sandbox/_apis/wit/workitems/$Code%20Review%20Request?api-version=7.1
     [{"op":"add","path":"/fields/System.Title","value":"probe0009 dormant-type write"}]
→ 400  TF401320: Rule Error for field Associated Context. Required, InvalidEmpty.
```
That 400 is a *field* rule, not a type block — the type accepted the write attempt. Proving it,
with the field rule bypassed:
```
POST …/Sandbox/_apis/wit/workitems/$Code%20Review%20Request?bypassRules=true&api-version=7.1
→ 200  {"id":655,"rev":1,"fields":{"System.WorkItemType":"Code Review Request",
        "System.State":"Requested", ...}}
```
**A work item of a "hidden" dormant type was successfully created via REST.** (Cleaned up:
`DELETE …/workitems/655?destroy=true` → `204`.)

Summary for ticket 0005: the four dormant types **cannot be disabled and cannot be deleted**,
because they are not process objects at all. They sit in `Microsoft.HiddenCategory`, which hides
them from the UI New menu and from backlogs but places **no restriction on the REST API**. If a
design needs those type names to be unwritable, ADO offers no lever — contrast the real block
available for `Hyperbright.*` types in §3.1/§3.2.

### 3.6 Renaming — **OPEN**

I did not probe a rename. What is measured and relevant: `referenceName` is fixed at creation
(`Hyperbright.WayfinderTask` for display name "Wayfinder Task" — the reference name strips
spaces and never tracks a later rename), and behaviour reference names likewise never change.
Whether `PATCH …/workitemtypes/{rn} {"name":...}` succeeds on a custom type is documented as a
supported UI action ("Change description, icon, or color" / Edit) but I did not measure it.

---

## 4. Fields

### 4.1 A field is org-global; its attachment to a type is separate — **[probe]**

```
POST B/workItemTypes/Hyperbright.Probe0009Scratch/fields?api-version=7.1-preview.2
     {"referenceName":"Custom.PriorityBand","required":false,"defaultValue":null,"readOnly":false}
→ 200
```
Verifying GET `B/workItemTypes/Hyperbright.Probe0009Scratch/fields/Custom.PriorityBand` → `200`.

Removing it from that one type:
```
DELETE B/workItemTypes/Hyperbright.Probe0009Scratch/fields/Custom.PriorityBand  → 204
```
Three verifying GETs:
- `B/workItemTypes/Hyperbright.Probe0009Scratch/fields/Custom.PriorityBand`
  → `404 VS402645: The field Custom.PriorityBand does not exists in work item type …` (removed)
- `…/_apis/wit/fields/Custom.PriorityBand?api-version=7.1` → `200` (org field intact)
- `B/workItemTypes/Hyperbright.Bug/fields/Custom.PriorityBand` → `200` (other types unaffected)

**Removing a field from one type is scoped to that type and does not delete it globally.**
Existing data: not measured on a type that actually held values — **OPEN**. **[doc]** for the
general behaviour is on the customize-process-field page; I did not extract it, so I am not
citing a specific line.

### 4.2 `allowedValues` on a `string` field comes from a **picklist**, and the process API hides it — **[probe]**

This is a real trap. The process-scoped field endpoints return a stub with no value information:
```
GET B/workItemTypes/Hyperbright.Bug/fields/Custom.VerificationMode?api-version=7.1-preview.2
→ {"referenceName":"Custom.VerificationMode","name":"Verification Mode","type":"string",
   "description":"","customization":"custom","isLocked":false}
```
No `allowedValues`, no `pickList`. Same for `$expand=all` on the list form, and same for
`…/_apis/wit/fields/Custom.VerificationMode` (org scope). The values are only visible on the
**project-scoped work-item-type field** endpoint with `$expand=all`:
```
GET …/Sandbox/_apis/wit/workitemtypes/Bug/fields/Custom.VerificationMode?$expand=all&api-version=7.1
→ {"allowedValues":["Not verified yet","Developer attested","Owner attested",
                    "Validation accepted","Validation proven to catch failure"],
   "alwaysRequired":false,"dependentFields":[], ...}
```
The backing object is a process picklist:
```
GET …/_apis/work/processes/lists?api-version=7.1-preview.1  → 11 picklists
GET …/_apis/work/processes/lists/5f48fbe6-d85d-4bf9-a77f-3e1faa223358?api-version=7.1-preview.1
→ {"name":"NiflheimVerificationMode","type":"String",
   "items":["Not verified yet","Developer attested","Owner attested",
            "Validation accepted","Validation proven to catch failure"],"isSuggested":false}
```
**Correction to the ticket's premise:** "VerificationMode has NO allowedValues in the API" is an
artifact of querying the process endpoint. It *does* have allowed values, they *are* enforced,
and `isSuggested:false` means the list is closed. Enforcement measured in §5.2.

### 4.3 Making a field always-required vs required-on-transition — **[probe + doc]**

Always-required is a field attribute (`alwaysRequired` / `required` on the type-field binding —
`System.State` shows `"required": true` on `GET B/workItemTypes/Hyperbright.Bug/fields`).
Required *only at a state* is **not** a field attribute — it is a rule. See §5.

---

## 5. Rules — and the Bug→Done gate mechanism

### 5.1 The Bug→Done gate is TWO custom process rules with a `when State = Done` condition — **[probe]**

Question areas 4 and 5, pinned exactly:
```
GET B/workItemTypes/Hyperbright.Bug/rules?api-version=7.1-preview.2
→ 200 {"count":2,"value":[
  {"id":"603f7fb3-409e-4020-a40e-1154a46370e2","name":"Require Verification Mode on close",
   "customizationType":"custom","isDisabled":false,
   "conditions":[{"conditionType":"when","field":"System.State","value":"Done"}],
   "actions":[{"actionType":"makeRequired","targetField":"Custom.VerificationMode","value":null}]},
  {"id":"308f8d79-f0bb-495d-b5b8-eeec172b36fe","name":"Require Falsification Criteria on close",
   "customizationType":"custom","isDisabled":false,
   "conditions":[{"conditionType":"when","field":"System.State","value":"Done"}],
   "actions":[{"actionType":"makeRequired","targetField":"Custom.FalsificationCriteria","value":null}]}]}
```
So the mechanism is: **`conditionType: "when"` on `System.State` = the target state, action
`makeRequired`.** It is a *state* rule, not a *transition* rule — it evaluates on the value of
`System.State` in the incoming revision, not on the from→to pair. Consistent with this, the
project-scoped workflow has no restricted transitions at all:
```
GET …/Sandbox/_apis/wit/workitemtypes/Bug?$expand=all&api-version=7.1
transitions: {"":[{"to":"To do"}],
  "To do":[{"to":"To do"},{"to":"Doing"},{"to":"Done"}],
  "Doing":[{"to":"To do"},{"to":"Doing"},{"to":"Done"}],
  "Done":[{"to":"To do"},{"to":"Doing"},{"to":"Done"}]}
```
Every transition is legal; the gate is entirely the two rules. There is no transition matrix to
edit in an inherited process.

**The gate really fires.** Created a Bug (default `To do`, id 654), then:
```
PATCH …/Sandbox/_apis/wit/workitems/654?api-version=7.1
      [{"op":"add","path":"/fields/System.State","value":"Done"}]
→ 400  TF401320: Rule Error for field Falsification Criteria. Required, InvalidEmpty.
       + Custom.VerificationMode: required, hasValues, limitedToValues, allowsOldValue, invalidEmpty
```
Verifying GET `…/workitems/654?fields=System.State` → `{"System.State":"To do"}` (rev 1). Refused
and nothing changed.

Note the flags on `Custom.VerificationMode`: `hasValues, limitedToValues` — that is the picklist
of §4.2 being enforced at the same time. Supplying an off-list value is refused even when the
field is populated:
```
PATCH …/workitems/654  [FalsificationCriteria=<div>probe</div>,
                        VerificationMode="probe-arbitrary-string", State=Done]
→ 400  The field 'Verification Mode' contains the value 'probe-arbitrary-string'
       that is not in the list of supported values
```

### 5.2 🔴 A process rule CAN be bypassed by a REST client — `bypassRules=true` walks straight through — **[probe]**

This is the most consequential finding in the memo.
```
PATCH …/Sandbox/_apis/wit/workitems/654?bypassRules=true&api-version=7.1
      [{"op":"add","path":"/fields/System.State","value":"Done"}]
→ 200  {"id":654,"rev":2,"fields":{"System.State":"Done", ...}}
```
Verifying GET `…/workitems/654?fields=System.State` → `{"System.State":"Done"}`, rev 2 —
**closed with both gate fields empty.** The item was later destroyed (`?destroy=true` → 204).

**[doc]** confirms the mechanism and its guard:
"users assigned the **Bypass rules on work item updates** project-level permission can save work
items without rules being evaluated. Rules can be bypassed … through the Work Items - update
REST API and setting the `bypassRules` parameter to `true`."
(https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops#bypass-rules)

So: **a custom rule is a guardrail against ordinary clients, not a security boundary.** Any
caller holding *Bypass rules on work item updates* can close a Bug with no falsification
criteria and no verification mode. Any design in tickets 0005/0007/0008 that treats a
`makeRequired` rule as an inviolable gate is wrong for a privileged automation identity — and
twig itself runs as one. The only enforcement measured here that survives `bypassRules` is
**type-disabling** (§3.2). Note the permission is project-level; whether it can be withheld from
the twig identity while leaving it able to write is **OPEN** (not probed).

### 5.3 Rule shape: conditions and actions — **[doc + probe]**

**[doc]** (rule-reference, "Rule composition", inheritance tab):
- max **2 conditions** and **10 actions** per rule;
- **all** conditions must be met (AND only — no OR);
- **only one condition is supported for state-transition rules**;
- you cannot have two rules with the same conditions *and* actions on the same WIT.

**[probe]** on condition vocabulary — the server validates `conditionType` against a closed enum:
```
POST B/workItemTypes/Hyperbright.Probe0009Scratch/rules
     conditions:[{"conditionType":"whenStateChangedTo","field":"System.State","value":"Done"}]
→ 400  VS1640105: Unrecognized value '$whenStateChangedTo' for property condition.conditionType.
```
whereas `whenWas` is accepted:
```
POST … conditions:[{"conditionType":"whenWas","field":"System.State","value":"To do"}]
       actions:[{"actionType":"makeRequired","targetField":"Custom.PriorityBand"}]
→ 201 {"id":"261e97f5-…","customizationType":"custom", ...}
```
Verifying GET `B/workItemTypes/Hyperbright.Probe0009Scratch/rules` → `count:1`, the rule as
posted. So **`whenWas` gives a genuine previous-value condition** — a real from→to transition
rule is expressible as `whenWas: <from>` + `when: <to>` (two conditions), though I did **not**
probe that pair firing at runtime — **OPEN**.

I did not enumerate the full condition/action enum. Known-good from live data: conditions
`when`, `whenWas`; actions `makeRequired`, `makeReadOnly` (accepted in a body), `setValue`
(name inferred from the `value` slot on the action object — **UNVERIFIED**). Documented condition
families are: work item is created / when the value of a field / when a field changes / user or
group membership. **Nothing in the documented or probed vocabulary references links, parents, or
child types** — consistent with `ado-parent-child-enforcement.md`.

---

## 6. States

### 6.1 A custom state can be added to a from-scratch type — **[probe]**

```
POST B/workItemTypes/Hyperbright.Probe0009Scratch/states?api-version=7.1-preview.1
     {"name":"Probing","color":"cc00cc","stateCategory":"InProgress","order":2}
→ 201 {"id":"b8664fb1-…","stateCategory":"InProgress","order":2,"customizationType":"custom"}
```
Verifying GET `…/states` → `count:4`: To do(Proposed,1), **Probing(InProgress,2)**,
Doing(InProgress,3), Done(Completed,4). Note **two states shared the `InProgress` category
without complaint** — a category is a bucket, not a slot.

### 6.2 A custom state CAN be added to an INHERITED type — **[probe]**

```
POST B/workItemTypes/Hyperbright.Task/states?api-version=7.1-preview.1
     {"name":"Probe0009Blocked","color":"cc00cc","stateCategory":"InProgress","order":2}
→ 201 {"id":"703431de-…","customizationType":"custom"}
```
Verifying GET `B/workItemTypes/Hyperbright.Task/states` → `count:4`, with the inherited
To Do/Doing/Done carrying `"customizationType":"system"` and the new one `"custom"`. So an
inherited type's workflow is **extensible but not replaceable**: you can add custom states
alongside the system ones; the system states remain marked `system`.

Reverted:
```
DELETE B/workItemTypes/Hyperbright.Task/states/703431de-7886-4104-8d26-a6f8dbde5889 → 204
```
Verifying GET → back to `count:3`, all three `"customizationType":"system"`. I did **not**
attempt to delete a `system` state — whether that is refused is **OPEN** (documented as
hide-only, not measured).

### 6.3 What the state category governs — **[doc]**

`stateCategory` is one of `Proposed` / `InProgress` / `Resolved` / `Completed` / `Removed`. It is
what boards, backlogs, burndown/velocity and "completed work" rollups key off — the *name* of a
state is cosmetic, the *category* is semantic. **[doc]**:
https://learn.microsoft.com/en-us/azure/devops/boards/work-items/workflow-and-state-categories?view=azure-devops
and rule-reference: "The *Completed* state category is not configurable, and is associated with
one and only one State."

This is the reason the Hyperbright naming works: `To do`/`Doing`/`Done` are custom names over
`Proposed`/`InProgress`/`Completed`, so every board mechanic behaves correctly despite the
non-standard vocabulary. Live confirmation across the process — Bug, Task, and the scratch type
all show the three categories exactly as expected in the GETs above.

---

## 7. Practical consequences for the map

1. **One backlog level per type, ever** (§2.2). A type that needs to appear at two levels needs
   two types — there is no other lever.
2. **A `makeRequired` gate is advisory against a privileged automation identity** (§5.2). twig
   writes as such an identity. Any "ADO will stop us" reasoning in 0007/0008 must be re-checked.
3. **Ticket 0005: the four dormant types cannot be touched** (§3.5). They are project-scope
   artifacts in `Microsoft.HiddenCategory`, invisible to the process API, and their hidden status
   does not stop a REST write. The only thing that *can* be locked down is a `Hyperbright.*`
   type, via `isDisabled` — which is the one block `bypassRules` respects.
4. **Disable, don't delete** (§3.3/§3.4). Deletion of a type with live items is refused; deletion
   of an empty custom type is instant and destructive.
5. **Read allowed values from the project WIT endpoint with `$expand=all`**, not the process
   endpoint (§4.2). The process endpoint returning a bare stub has already produced one wrong
   conclusion on this board.

---

## 8. What this memo does NOT establish (honest gaps)

- **Rename of a custom type** — not probed at all (§3.6).
- **Whether `inherits` can be changed after creation** — not probed (§1.4).
- **Whether an inherited type with zero work items can be deleted** — the only refusal I got was
  `ActiveWorkItemsExistException`, which is about data, not inheritance (§3.4).
- **Full condition/action enum for rules** — only `when` and `whenWas` confirmed accepted,
  `whenStateChangedTo` confirmed rejected. `setValue` is inferred, not measured (§5.3).
- **A two-condition `whenWas` + `when` transition rule firing at runtime** — the rule was
  accepted by the API but never exercised against a live work item (§5.3).
- **What happens to existing field data when a field is removed from a type** — the removal was
  probed on a type with no work items (§4.1).
- **Whether the "Bypass rules on work item updates" permission can be withheld from the twig
  identity** — not probed (§5.2).
- **The 405-on-PATCH claim for behaviours** — inherited from the ticket, not reproduced; what I
  measured on the `workitemtypesbehaviors` route was a 500 (§2.1).

## 9. Probe hygiene

Everything created was removed, and the removal was verified:
- work items 654 (Bug) and 655 (Code Review Request) — `DELETE …?destroy=true` → `204` each.
- custom state `Probe0009Blocked` on `Hyperbright.Task` — `DELETE` → `204`; verifying GET shows
  Task back to its 3 original system states.
- type `Hyperbright.Probe0009Scratch` (with its state, its field binding and its rule) —
  `DELETE` → `204`; verifying GET → `VS1640142 not found`.
- Final verifying `GET B/workitemtypes` → **16 types**, byte-for-byte the starting set
  (13 `Hyperbright.*` + 3 `Microsoft.VSTS.WorkItemTypes.Test*`), `Hyperbright.Issue` still the
  only disabled one.

No mutation was aimed at the `Twig` project.
