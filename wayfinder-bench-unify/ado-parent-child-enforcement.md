# Can Azure DevOps Services (inherited process) enforce type-level parent/child policy?

Research date: 2026-08-22. Target: Azure DevOps **Services** (cloud), org `PolyphonyRequiem`, inherited process "Hyperbright" (`ba4e268d-7d67-43bd-8065-df7ab52fba0c`), derived from Basic.

Every claim below is cited to a Microsoft primary source. Where documentation is **silent**, that is stated explicitly as "not documented; verify empirically" rather than inferred.

---

## VERDICT

- **No.** There is no documented mechanism in Azure DevOps Services' inherited process model that restricts *which work item type may be the parent of which other work item type*. Custom rules operate exclusively on **fields** of a single work item; the complete documented condition and action enumerations contain nothing that references a link, a parent, or a parent's type ([Rule Condition Type / Rule Action Type enums](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/rules/add?view=azure-devops-rest-7.1); [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops)).
- The **only** documented constraint on the hierarchy link type itself is topological, not type-based: `System.LinkTypes.Hierarchy` is a *Tree* topology — one parent per item, no circular references — with no statement restricting which types may participate ([Link type reference](https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference?view=azure-devops)).
- The **closest** thing to enforcement is the **backlog-level hierarchy** (portfolio backlog levels + their work item types). Documentation describes it as governing what the UI *offers* and how reparenting behaves on backlogs — "You can only reparent backlog items under other features, and features under other epics" ([Organize your backlog](https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/organize-backlog?view=azure-devops)). Its limits are fatal for your use case: custom portfolio backlogs can only be added as a **new top level** ([Customize backlogs and boards](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-backlogs-boards?view=azure-devops)), max 5 portfolio levels ([Object limits](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits?view=azure-devops)), and it does **not** stop link creation via the REST API or the Links tab — consistent with your empirical finding that a `Map` accepted `Grilling` children at the same backlog level.
- **No synchronous veto exists.** The documented work-item form extensibility events are `onFieldChanged`, `onLoaded`, `onUnloaded`, `onSaved`, `onReset`, `onRefreshed` — `onSaved` is **past tense**; there is no documented pre-save/cancellable event ([Extend the work item form](https://learn.microsoft.com/en-us/azure/devops/extend/develop/add-workitem-extension?view=azure-devops)). Service hooks are explicitly described as one-way notification/action triggers fired *when events happen* ([Service hooks overview](https://learn.microsoft.com/en-us/azure/devops/service-hooks/overview?view=azure-devops)).
- **Recommendation:** if the team needs "a Conception may only parent Ideas", it must be enforced in their own CLI/tooling (or a post-hoc detector fed by `workitem.updated` service hooks). Do not plan on the platform doing it.

---

## 1. Custom rules on work item types

### What rules are

> "For an inherited process, each rule consists of two parts: Conditions and Actions. Conditions define the circumstances which must be met in order for the rule to be applied. Actions define the operations to perform."
> — [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops)

Same page, scoping statement:

> "Rules are used to set or restrict value assignments to a **work item field**." … "Each constraint operates on a **single field**. Constraints are evaluated on the server on work item save, and if any constraint is violated the save operation is rejected."

That is the crux: the rule engine's unit of operation is a field on the work item being saved. Nothing in the documentation extends it to links or to related work items.

### Complete documented CONDITION list

From the REST contract `Rule Condition Type` enumeration — [Rules - Add, REST API 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/rules/add?view=azure-devops-rest-7.1) — verbatim value list:

```
when
whenNot
whenChanged
whenNotChanged
whenWas
whenStateChangedTo
whenStateChangedFromAndTo
whenWorkItemIsCreated
whenValueIsDefined
whenValueIsNotDefined
whenCurrentUserIsMemberOfGroup
whenCurrentUserIsNotMemberOfGroup
```

With the documented descriptions for the four core ones (verbatim):

> `when` — "$When. This condition limits the execution of its children to cases when another field has a particular value, i.e. when the Is value of the referenced field is equal to the given literal value."
> `whenNot` — "$WhenNot. This condition limits the execution of its children to cases when another field does not have a particular value…"
> `whenChanged` — "$WhenChanged. This condition limits the execution of its children to cases when another field has changed, i.e. when the Is value of the referenced field is not equal to the Was value of that field."
> `whenNotChanged` — "$WhenNotChanged. … when the Is value of the referenced field is equal to the Was value of that field."

The `RuleCondition` object schema has exactly three members: `conditionType`, `field` (string — "Field that defines condition"), `value` (string). **There is no `linkType`, `targetWorkItem`, `parentType`, or equivalent member.** (Same REST page.)

The UI-facing condition list on [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops) matches:

> "The value of ... (equals) [When]" / "A change was made to the value of ... [WhenChanged]" / "The value of ... (not equals) [WhenNot]" / "No change was made to the value of ... [WhenNotChanged]"

plus the membership conditions:

> "Current user is a member of group ..." / "Current user is not member of group ..."

### Complete documented ACTION list

From `Rule Action Type` enumeration — [Rules - Add, REST API 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/rules/add?view=azure-devops-rest-7.1) — verbatim, with descriptions:

| Value | Documented description |
|---|---|
| `makeRequired` | "Make the target field required." |
| `makeReadOnly` | "Make the target field read-only." |
| `setDefaultValue` | "Set a default value on the target field…" |
| `setDefaultFromClock` | "Set the default value on the target field from server clock." |
| `setDefaultFromCurrentUser` | "Set the default current user value on the target field." |
| `setDefaultFromField` | "Set the default value on from existing field to the target field." |
| `copyValue` | "Set the value of target field to given value." |
| `copyFromClock` | "Set the value from clock." |
| `copyFromCurrentUser` | "Set the current user to the target field." |
| `copyFromField` | "Copy the value from a specified field and set to target field." |
| `setValueToEmpty` | "Set the value of the target field to empty." |
| `copyFromServerClock` | "Use the current time to set the value of the target field." |
| `copyFromServerCurrentUser` | "Use the current user to set the value of the target field." |
| `hideTargetField` | "Hides target field from the form. This is a server side only action." |
| `disallowValue` | "Disallows a field from being set to a specific value." |

The `RuleAction` object schema has exactly three members: `actionType`, `targetField` ("Field on which the action should be taken"), `value`. **Every action targets a field.** There is no "block link", "reject save", "prevent parenting", or free-form error-message action.

The UI list in [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops) agrees: "Clear the value of ...", "Copy the value from ...", "Make read-only ...", "Make required ...", "Set the value of ...", "Use the current time to set the value of ...", "Use the current user to set the value of ...", "Hide the field ...", "Restrict the transition to state ...".

### Sample scenarios

[Sample custom rule scenarios](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-samples?view=azure-devops) and [Add a rule to a work item type](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/custom-rules?view=azure-devops) list scenarios such as: "When a value is defined for Priority, make Risk a required field", "When a change is made to the value of Release, clear the value of Milestone", "When current user isn't a member of Project Administrators, hide the Priority field." **No sample involves a link, a parent, or a child.**

### Answer to avenue 1

**No.** No documented condition can reference the parent work item or a parent's type, and no documented action can block a link. The rules docs contain no mention of parent/child link enforcement at all.

Also relevant: rules can be bypassed outright.

> "users assigned the **Bypass rules on work item updates** project-level permission can save work items without rules being evaluated… through the Work Items - update REST API and setting the `bypassRules` parameter to true."
> — [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops)

So even a hypothetical field-based proxy rule would not be an absolute guarantee.

Note also that the XML-only elements that come nearest to relational constraints (`FROZEN`, `MATCH`, `NOTSAMEAS`) are **explicitly unsupported** in the inherited process — and none of them constrain links anyway:

> "Other XML elements, such as FROZEN, MATCH, NOTSAMEAS, aren't supported in the inherited process."
> — same page.

---

## 2. Link type rules / restrictions

[Link type reference](https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference?view=azure-devops) documents, for the hierarchy link type:

> "Reference Name: `System.LinkTypes.Hierarchy`; Names: Child, Parent; Topology: Tree; Is Active: True"

and, on link types generally:

> "Each work link type defines labels, topology, and restrictions used when you construct links between work items. For example, the parent-child link type defines two labels: Parent and Child. The link type uses a **tree topology and prevents circular references** between work items."

The documented attributes of a link type are: whether it "allows or (`true`) or restricts (`false`) circular relationships", whether it "allows for more than one target (`false`) or is restricted to a single target (`true`)", and the topology type "`dependency`, `network`, and `tree`". **Type-of-endpoint is not among the attributes.**

There is no documented process-level or project-level setting for "which work item types may participate in link type X". Work item link types in Azure DevOps Services are **system-defined** and are not customizable through the inherited process model — the process customization surface documented under [Customize an inherited process](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process?view=azure-devops) covers work item types, fields, layout, rules, states, backlog levels and portfolio backlogs; **link types are not listed as a customizable object.**

**Answer: No.** *(Statement of absence: no such setting is documented anywhere in the process-customization docs. This is an absence-of-documentation finding, not a positive statement that the server internally lacks the capability.)*

---

## 3. The `System.Parent` field

`Parent` **is** a real field:

> "**Parent** — … When included as a column option in a backlog or query results list, the system displays the **Title** of the parent work item. Internally, the system stores the **ID** of the work item in an Integer field. … You can add the **Parent** field as a column or specify it within a query clause by specifying the parent work item ID. **Reference Name**=`System.Parent`, **Data type**=Integer"
> — [Link work items, Parent field](https://learn.microsoft.com/en-us/azure/devops/boards/queries/linking-attachments?view=azure-devops#parent)

So: **queryable** (WIQL clause / column) — documented. It appears in the [Work item field index](https://learn.microsoft.com/en-us/azure/devops/boards/work-items/guidance/work-item-field?view=azure-devops).

Critically, it stores an **Integer ID**, not a type name. Even if a rule could condition on it, the value carries no type information — a rule could at best test equality against one literal work item ID, which is useless as a *policy*.

Can a rule condition on it / fire on "when System.Parent changes"? **Not documented.** The rule docs constrain system fields as follows:

> "The rule engine **restricts setting conditions or actions to system fields** except as follows: You can make **State** and **Reason** fields read-only. You can apply most rules to the **Title**, **Assigned To**, **Description**, and **Changed By** fields.
> If you don't see a field listed in the drop-down menu of the rule user interface for the Inheritance process, this is why. … Even if you're able to specify a system field, the rule engine may restrict you from saving the rule."
> — [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops)

`System.Parent` is a `System.*` field and is **not** in the allow-list (Title, Assigned To, Description, Changed By, plus read-only on State/Reason). The documented reading is therefore that `System.Parent` is **restricted** from use in rule conditions/actions. **However, Microsoft does not state this about `System.Parent` explicitly — it is covered only by the general restriction clause. Treat "you cannot write a `$whenChanged` rule on `System.Parent`" as strongly indicated but NOT explicitly documented; verify empirically** by attempting to POST a rule with `{"conditionType":"$whenChanged","field":"System.Parent"}` against the Hyperbright process and observing whether the API rejects it.

Also note: even in the best case this would only tell you *that* the parent changed, never *what type* the new parent is. No documented mechanism surfaces the parent's `System.WorkItemType` into the child's rule evaluation context.

**Settable?** Not documented as directly settable via a field patch; the hierarchy is manipulated through the `/relations` collection of the Work Items - Update API ([Work Items - Update](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1)). Whether `System.Parent` accepts a direct JSON-Patch write is **not documented; verify empirically.**

---

## 4. "Valid child types" / `childTypes`

Searched the Processes REST API surface: [Work Item Types - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/work-item-types/get?view=azure-devops-rest-7.1), [Behaviors - List](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/behaviors/list?view=azure-devops-rest-7.1), [Lists](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/lists?view=azure-devops-rest-7.1), [Work Item Types Field](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/work-item-types-field?view=azure-devops-rest-7.1), and the WIT-level [Work Item Types - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/get?view=azure-devops-rest-7.1).

Findings:

- The process **Work Item Type** object's documented members are: `behaviors`, `color`, `customization`, `description`, `fields`, `icon`, `id`, `inherits`, `isDisabled`, `layout`, `name`, `referenceName`, `states`, `url`. **There is no `childTypes` / `validChildTypes` member.** ([Processes / Work Item Types - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/work-item-types/get?view=azure-devops-rest-7.1))
- The WIT-level `WorkItemType` object exposes `fields`, `fieldInstances`, `states`, `transitions`, `xmlForm`, `icon`, `color`, `isDisabled`, `referenceName`, `name`, `description`. **No child-type declaration.** ([WIT / Work Item Types - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-item-types/get?view=azure-devops-rest-7.1))
- **Behaviors** are what carry hierarchy semantics, and they are explicitly *backlog levels*. The documented behavior descriptions in [Behaviors - List](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/behaviors/list?view=azure-devops-rest-7.1) are verbatim: `"Requirement level backlog and board"`, `"Epic level backlog and board"`, `"Feature level backlog and board"`, `"Task level backlog and board"`, `"Portfolio level backlog and board"`, and `"Enables work items to be ordered relative to other work items"` — each with a numeric `rank` (10/20/30/40/50) and an `inherits` reference. A behavior associates a WIT with a **backlog level**; it does not enumerate permitted child types.
- The nearest thing to a machine-readable hierarchy is [Backlog Configuration - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/work/backlogconfiguration/get?view=azure-devops-rest-7.1), which returns `taskBacklog`, `requirementBacklog`, `portfolioBacklogs[]` — each with a `rank`, a `workItemTypes[]` list, and `workItemCountLimit`. The ordering of these ranked levels + their `workItemTypes` is the hierarchy the Boards UI uses.

**Conclusion for avenue 4:** No API on Microsoft Learn documents a per-work-item-type `childTypes` declaration independent of backlog level. **The most plausible origin of a `valid_child_types` list in a CLI is derivation from Backlog Configuration ranks (level *n* → the `workItemTypes` of level *n−1*) — but Microsoft does not document any endpoint literally named or returning `childTypes`/`validChildTypes`, so if your CLI reads such a key it is either (a) computing it locally, (b) reading it from a non-public/undocumented internal endpoint used by the Boards web UI, or (c) reading its own config. This is not documented; determine it empirically by grepping the CLI source for the key and, if it hits an HTTP call, logging the URL.** Do not assume it is a server-side policy surface.

---

## 5. Server-side extensibility — is a synchronous veto possible?

### Service hooks / webhooks: **no veto.**

> "You can use service hooks to run tasks on other services **when events happen** in your Azure DevOps project." … "Service hook **publishers** define a set of *events* that you can subscribe to. **Subscriptions** listen for these events and define **actions** to take based on events."
> — [Service hooks overview](https://learn.microsoft.com/en-us/azure/devops/service-hooks/overview?view=azure-devops)

The work item events available are `workitem.created`, `workitem.updated`, `workitem.deleted`, `workitem.restored`, `workitem.commented` ([Service hooks events](https://learn.microsoft.com/en-us/azure/devops/service-hooks/events?view=azure-devops)). All are **past-tense notifications**. There is no documented response contract by which a subscriber returns a rejection, and no documented "pre-save" event. Service hooks are **reactive only** — a hook can detect an illegal parent link after the fact and (via the REST API) remove it or flag it, but cannot prevent it.

### Work item form extensibility: **no cancellable pre-save event.**

The documented `ms.vss-work-web.work-item-notifications` observer/listener contract exposes exactly these callbacks:

```
onFieldChanged
onLoaded
onUnloaded
onSaved
onReset
onRefreshed
```

— [Extend the work item form](https://learn.microsoft.com/en-us/azure/devops/extend/develop/add-workitem-extension?view=azure-devops)

Note `onSaved` — **past tense**. The same page states: "Observers listen to work item events **without any UI** on the form. Use observers to listen for the `onSaved` event, since observers live outside the form and aren't destroyed when the form dialog closes."

There is **no documented `onSave` / `beforeSave` hook and no documented mechanism for an extension to return `false` or throw to cancel a save.** Additionally, form extensions are **client-side, browser-only** — they are not evaluated when a work item is updated via the REST API, `az boards`, or any other non-web client, so they could never be an enforcement boundary even if a veto existed. (The rules doc contrasts this explicitly for *rules*: "Rules are always enforced, not only when you are interacting with the form but also when interfacing through other tools." — [Rules and rule evaluation](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/rule-reference?view=azure-devops). No such statement is made about form extensions.)

### Server-side plugins (TFS `ISubscriber` / `ISubscriber`-style event handlers)

Server-side plugins were an **on-premises Team Foundation Server / Azure DevOps Server** capability: unmanaged .NET assemblies dropped into the application tier's plugin directory, which could subscribe to events *decidedly* (returning `EventNotificationStatus.ActionDenied`). **Azure DevOps Services (cloud) does not expose an application tier and Microsoft publishes no equivalent capability.** The current [Azure DevOps extensibility documentation](https://learn.microsoft.com/en-us/azure/devops/extend/overview?view=azure-devops) describes only web-based extensions (contributions, hubs, form controls, pipeline tasks, service hooks) — there is no server-side/in-process execution model documented for Services.

**Caveat, stated plainly:** Microsoft does not publish a page that says "server-side plugins are not supported in Azure DevOps Services." The finding here is an **absence of any documented server-side plugin surface for Services**, combined with the architectural fact that the cloud service exposes no application tier. Do not represent this as an explicit Microsoft statement.

**Answer to avenue 5: No synchronous veto is available in Azure DevOps Services by any documented mechanism.** Only after-the-fact detection and remediation.

---

## 6. Boards / backlogs as a soft guard — what it actually does

What the backlog hierarchy **does** constrain (documented):

- **Reparenting on a backlog is level-constrained:**
  > "You can only reparent backlog items under other features, and features under other epics."
  > — [Organize your backlog](https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/organize-backlog?view=azure-devops)
- **The "add child" affordance follows the configured hierarchy:**
  > "You can add child items to your features from any backlog. … When you see the **Add** icon, you can add a child item. **The work item always corresponds to the hierarchy of work item types defined for your project.**"
  > — [Define features and epics](https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/define-features-epics?view=azure-devops)

  i.e. the type created by the "+" button is determined by the next level down, not chosen freely. This is the strongest documented "guard" — but it constrains what the *button creates*, not what links the *system accepts*.
- **Mapping pane** likewise maps items to the next level up ([Organize your backlog](https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/organize-backlog?view=azure-devops)).

What it **does not** constrain (and the documented reasons):

- The hierarchy is defined by **portfolio backlog levels**, and custom levels can only be added at the top:
  > "From the Backlog levels page, choose **+ New top level portfolio backlog**." … "You can add custom portfolio backlogs **up to a total of five portfolio backlogs**."
  > — [Customize backlogs and boards](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-backlogs-boards?view=azure-devops), [Object limits](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits?view=azure-devops) ("Portfolio backlog levels per process: 5")

  So you cannot express arbitrary type-pair policies as levels, and you cannot insert a level between existing ones. This matches the prior research pass.
- **A backlog level maps to a *set* of work item types.** Backlog Configuration returns `workItemTypes[]` per level ([Backlog Configuration - Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/work/backlogconfiguration/get?view=azure-devops-rest-7.1)). Any type at level *n* can therefore parent any type at level *n−1* — the model has **no expressive room** for "Conception parents only Ideas" when Conception and Idea share a level with other types.
- **The Links tab / REST API are not documented as level-constrained.** No Microsoft page states that adding a `System.LinkTypes.Hierarchy-Reverse` relation via the work item form's Links tab or via [Work Items - Update](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1) validates backlog levels. **The absence of such validation is not positively documented either — but your own empirical result (a `Map` with `Grilling` children, both on the same backlog level, link accepted) is direct evidence that same-level parenting is accepted.**
- Whether the *drag* gesture in the backlog UI *refuses* an out-of-level drop versus silently permitting it is described only by the single sentence "You can only reparent backlog items under other features, and features under other epics." **The exact UI failure mode (drop rejected vs. error toast vs. accepted-and-reordered) is not documented; verify empirically if it matters.**

---

## Summary table

| Avenue | Can it enforce type-level parent/child policy? | Basis |
|---|---|---|
| 1. Custom rules (inherited process) | **No** | Conditions/actions target fields only; full enums contain no link/parent construct |
| 2. Link type restrictions | **No documented setting** | Link types are system-defined; only topology/circularity/single-target are documented attributes |
| 3. `System.Parent` field | **No** — queryable, Integer ID only, no type info; rule use restricted by the system-field clause (not explicitly documented for this field — verify empirically) | Field index / linking-attachments; rule-reference system-field restriction |
| 4. `childTypes` / valid child types API | **No such documented API**; hierarchy is expressed via behaviors + Backlog Configuration ranks | Processes WIT/Behaviors schemas; Backlog Configuration - Get |
| 5. Server-side extensibility / service hooks | **No synchronous veto**; reactive only. No documented server-side plugin surface for Services | Service hooks overview/events; form extension event list (`onSaved`, past tense) |
| 6. Backlogs/boards | **Soft guard only** — governs the "add child" type and backlog reparenting; cannot express same-level or arbitrary type-pair policy; does not gate the API | Organize your backlog; Define features and epics; Customize backlogs and boards; Object limits |

## Suggested empirical checks (not yet performed)

1. `POST .../_apis/work/processes/{processId}/workItemTypes/{witRefName}/rules` with `{"conditionType":"$whenChanged","field":"System.Parent"}` — confirm rejection.
2. Inspect the New rule dialog's field dropdown for the Hyperbright process — confirm `Parent` is absent.
3. Attempt a cross-level hierarchy link via `PATCH .../_apis/wit/workitems/{id}` adding `System.LinkTypes.Hierarchy-Reverse` between deliberately mismatched types — confirm acceptance.
4. Grep the team's CLI for `valid_child_types` and log the HTTP call (if any) that populates it.
