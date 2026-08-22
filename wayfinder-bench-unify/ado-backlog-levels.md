# Azure DevOps backlog levels (backlog behaviors) — constraints for INHERITED processes

Scope: Azure DevOps **Services** (cloud), **Inheritance** process model. Every claim below is followed by the
primary Microsoft Learn URL that owns it. Where docs are silent, this file says so explicitly.

Sources used (all first-party Microsoft Learn):

- BB = https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-backlogs-boards?view=azure-devops ("Customize backlogs and boards (Inheritance process)")
- WIT = https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process-work-item-type?view=azure-devops ("Add and manage work item types")
- CP = https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process?view=azure-devops
- BEH = https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/behaviors?view=azure-devops-rest-7.1 and .../behaviors/create?view=azure-devops-rest-7.1
- WITB = https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/work-item-types-behaviors/add?view=azure-devops-rest-7.1
- LTR = https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-type-reference?view=azure-devops
- REORD = https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/resolve-backlog-reorder-issues?view=azure-devops
- ORG = https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/organize-backlog?view=azure-devops
- PLAN = https://learn.microsoft.com/en-us/azure/devops/boards/plans/review-team-plans?view=azure-devops
- PLAN2 = https://learn.microsoft.com/en-us/azure/devops/boards/plans/add-edit-delivery-plan?view=azure-devops

---

## DIRECT ANSWER

- **You cannot create a backlog level between Feature and Task.** Microsoft states plainly: *"You can't insert a new custom backlog level within the existing set of defined backlogs. The predefined backlog levels are typically fixed, for example Epics, Features, User stories, and Tasks."* ([BB]) The only creation affordance is **`+ New top level portfolio backlog`** — i.e. a new level always lands **above** the current top portfolio level ([BB]). So the proposed "tier between Feature and Task" is **impossible as a backlog level**.
- **Therefore: these are Requirement-level types, not a new level.** Construction / Validation / Documentation / wayfinder ticket types should be added to the **Requirement backlog** (Edit backlog dialog for the Requirements backlog) ([BB]), or to the **Iteration backlog** if they are task-sized — *"You can't create a custom task-specific backlog level, but you can still add custom WITs to the iteration backlog."* ([BB])
- **Map and Feature cannot both sit at Feature level *and* have the new types as a distinct level below.** A WIT belongs to exactly one level: *"You can't add a WIT to two different backlog levels. Each WIT can belong to only one backlog level."* ([BB]) Map + Feature together at the Features level, with the new types at Requirements level, is the shape that fits the product.
- **Parent-child linking is NOT enforced by backlog level.** `System.LinkTypes.Hierarchy` is a tree link whose only documented restriction is *"A work item can have only one Parent. A parent work item can have many children."* ([LTR]) Same-level parenting is explicitly *possible but unsupported for ordering*: *"The natural hierarchy breaks when you create same-category or same-type links between work items… it results in a nested item that disables the ordering feature."* ([REORD]) This is exactly the observed Map→Grilling case: the link was legal, the backlog ordering was degraded.
- **Cost of the cheap path is low; cost of the level path is high and capped.** Max **five** portfolio backlogs total ([BB]); inherited levels can never be removed, only renamed/disabled ([BB]); deleting a level *"removes the backlog and board associated with the level for all teams, including customizations made to them"* ([BB]).
- **Delivery Plans only show product + portfolio backlogs.** *"Work items belong to the team's product backlog or portfolio backlog. Only work item types selected for viewing on a team's backlog appear on the plan."* ([PLAN]) Requirement-level types WILL appear in plans; iteration/Task-level types are not documented as selectable — see Q5.

---

## Q1 — Can you add a custom backlog level at all in an inherited process?

**Yes — but only as a top-level portfolio backlog.**

> "In your project, you currently have two predefined portfolio backlogs: Features and Epics. If your project requires more portfolio backlogs, you can create them." ([BB])

> "The standard product, iteration, and portfolio backlogs inherited from system processes are fully customizable. You can also add custom portfolio backlogs up to a total of five portfolio backlogs." ([BB])

**UI path** ([BB]):
1. Sign in to `https://dev.azure.com/{Your_Organization}`
2. **Organization settings** → **Process**
3. Select the process → **Backlog levels** page
4. Choose **`+ New top level portfolio backlog`**
5. "Name the backlog level, select the backlog level color, and add the work item type to associate with this level, and then select **Add**."

Also confirmed in CP: "You can add more work item types (WITs) to a backlog level or create another portfolio backlog." … "From the Process page, select your inherited process and then select **Backlog levels**." ([CP])

**REST endpoints** (Processes → Behaviors, api-version 7.1):
- Create a level: `POST https://dev.azure.com/{organization}/_apis/work/processes/{processId}/behaviors?api-version=7.1` ([BEH create]). The request body carries `name`, `color`, and `inherits` — the documented example creates a behavior with `"inherits": "System.PortfolioBacklogBehavior"` and the response reference name is auto-generated as `Custom.<guid>` (e.g. `Custom.4b8fdba0-7064-458d-b55c-522b39059a62`) ([BEH create]).
- List: `GET .../_apis/work/processes/{processId}/behaviors?api-version=7.1` ([BEH]).
- Attach a WIT to a level: `POST https://dev.azure.com/{organization}/_apis/work/processes/{processId}/workitemtypesbehaviors/{witRefNameForBehaviors}/behaviors?api-version=7.1`, body `{ "behavior": { "id": "<behaviorRefName>" }, "isDefault": <bool> }` ([WITB]).

Note on `customization` values, per the REST reference: *"System behaviors are inherited from parent process but not modified. Inherited behaviors are modified behaviors that were inherited from parent process. Custom behaviors are behaviors created by user in current process."* ([BEH create]) This confirms the reading of your process dump: no `custom` behavior exists yet in Hyperbright.

**Whether the REST `create` API can produce a non-portfolio (mid-hierarchy) level is not documented.** The only documented `inherits` value in the reference example is `System.PortfolioBacklogBehavior` ([BEH create]); the reference does not enumerate the legal set of `inherits` values. Combined with the UI-level prohibition in [BB], there is **no primary source suggesting a mid-hierarchy insertion is possible**, and none suggesting the REST API bypasses it. Treat "REST can do what the UI can't" as **not documented; verify empirically** if you care — but do not plan on it.

---

## Q2 🔴 — WHERE can a custom backlog level be inserted?

**The belief is CONFIRMED. Custom levels can only be added at the top, as portfolio backlogs. You cannot insert a level between Requirement and Task.**

Exact statements, from the **Limitations** section of [BB]:

> "You can't insert a new custom backlog level within the existing set of defined backlogs. The predefined backlog levels are typically fixed, for example Epics, Features, User stories, and Tasks."

> "You can't reorder the backlog levels. They usually follow a predefined hierarchy, and changing the order isn't supported."

> "You can't create a custom task-specific backlog level, but you can still add custom WITs to the iteration backlog. For example, you could create a custom WIT called Enhancement or Maintenance and associate it with the iteration backlog."

> "You can't remove an inherited portfolio level from a product. You can rename the level, or disable WITs to prevent teams from creating new work items of those types."

> "You can't add a WIT to two different backlog levels. Each WIT can belong to only one backlog level."

And the creation affordance itself is named for the constraint:

> "From the Backlog levels page, choose **+ New top level portfolio backlog**." ([BB])

**Unambiguous conclusion:** a tier between Feature and Task **cannot exist as a backlog level** in an inherited process. The wayfinder types must be either:
- **Requirement-level types** — added via "Edit or rename the requirement backlog": *"The Requirement backlog, also referred to as the product backlog, defines the work item types that appear on the product backlog and board… You can rename the backlog, change the color, add work item types, and change the default work item type."* ([BB]) The doc's own worked example does exactly this: *"we renamed the backlog, added Customer Ticket and Issue, and changed the default type to Customer Ticket."* ([BB]); or
- **Iteration/Task-level types** — *"For the iteration backlog, you can add work item types and change the default work item type."* ([BB]); or
- **no level at all** (see Q6).

Corollary for the Epic → (Map, Feature) → (Construction, Validation, Documentation) → Task shape: the only way to realise it inside supported behaviour is **Map at the Features level alongside Feature**, and **the new types at the Requirements level**. Note this leaves *no* supported level between Map and Feature either — they are siblings on the same level.

---

## Q3 — How many portfolio backlog levels are permitted?

**Five total.**

> "You can also add custom portfolio backlogs up to a total of five portfolio backlogs." ([BB])

Read together with "The Agile, Scrum, and CMMI system processes define two default portfolio backlogs, Epics and Features… The Basic process only defines the Epics backlog and Epic work item type." ([BB]) — i.e. for an Agile-derived process, two of the five are already consumed by Features and Epics. **Whether the count of five includes the inherited Features/Epics levels is stated as "a total of five portfolio backlogs", which reads as inclusive; the doc does not restate it more precisely.** Treat the exact accounting as: five portfolio levels inclusive, per the plain wording of [BB].

(Separate, unrelated numeric limit — Delivery Plans: "You can add up to 20 backlog levels for Azure DevOps Services or 15 backlog levels for Azure DevOps Server 2022." ([PLAN2]) That is a per-plan row limit, not a process limit.)

---

## Q4 — What does a backlog level control vs what parent-child linking controls?

### What the backlog level controls (documented)

1. **Which WITs appear on which backlog/board.** "The Requirement backlog… defines the work item types that appear on the product backlog and board." ([BB]) "The Iteration backlog… defines the work item types that are displayed on the sprint backlogs and Taskboards." ([BB])
2. **Which hidden fields get injected into the WIT.** "When you add a WIT to a backlog level, certain fields are automatically added to the WIT definition as hidden fields." Portfolio → Stack Rank / Backlog Priority; Requirement → Stack Rank + Story Points (Agile) / Size (CMMI) / Effort (Scrum); Iteration → Activity, Remaining Work, Stack Rank. "Remaining Work is used in Sprint burndown and capacity charts." ([BB])
3. **The default/offered parent for the "add child"/mapping affordance.** "The backlog level to which you add a custom work item type determines the parent work item types for the work item type." ([WIT], FAQ note)
4. **Reordering/nesting semantics on the backlog.** See below.

### What parent-child linking controls (documented)

`System.LinkTypes.Hierarchy-Forward` / `-Reverse` is a **tree**-topology link. Its documented restrictions ([LTR]):

> "Use this directional link to create one-to-many relationships between a single parent and one or more child items."

> "A work item can have only one Parent. A parent work item can have many children."

> "…the parent-child link type defines two labels: Parent and Child. The link type uses a tree topology and prevents circular references between work items."

**There is no documented restriction in [LTR] that the parent and child must be on adjacent backlog levels, or on different backlog levels.** The only enumerated restrictions are single-parent and no-circularity.

### Same-level and non-adjacent parenting: possible, but degrades backlog behaviour

Microsoft's troubleshooting article documents same-level parenting as something users **do** create, and describes the consequence rather than forbidding it ([REORD]):

> "When you reorder, nest, and display work items, Azure Boards expects a natural hierarchy. The natural hierarchy breaks when you create same-category or same-type links between work items. For example, parent-to-child links that are bug to bug or user story to user story."

> "The following image shows a bug as a child of a user story. When the backlog displays user stories and bugs at the same level (Requirements category), it results in a nested item that disables the ordering feature."

> Error text quoted by the doc: "You can't reorder work items and some work items might not be shown. See work item 7 to either remove the parent to child link or change the link type to Related." / "Work item 3 can't be reordered because its parent is on the same category."

> Guidance: "Only create parent-child links one level deep between items that belong to different categories." and "Establish same-category hierarchies… don't create story-story, bug-bug, task-task, or issue-issue links. The backlog, board, and sprints experiences don't support reordering for same-category hierarchies…"

**So, answering the question directly:** the backlog-level hierarchy does **not** constrain link creation at the data layer — it constrains the **backlog UI's ordering and display**. The observed real Map work item with Grilling children on the same level is consistent with this documentation: the link was permitted, and what breaks is reordering/display on that backlog (error messages above).

**Non-adjacent-level parenting (e.g. Epic → Task, skipping levels): the docs do not state whether such a link is permitted or rejected.** [LTR] lists no such restriction, and [REORD] addresses only same-category links. **Not documented; verify empirically.**

The **UI reparent affordance**, separately, *is* level-constrained: "You can only reparent backlog items under other features, and features under other epics." ([ORG]) That is a statement about the backlog drag/mapping UI, not about the link type.

---

## Q5 — What appears in Delivery Plans?

> "Work items belong to the team's product backlog or portfolio backlog. Only work item types selected for viewing on a team's backlog appear on the plan." ([PLAN], Prerequisites)

> "Delivery Plans supports the following tasks: View up to 20 team backlogs… **Add custom portfolio backlogs and Epics.** … View rollup progress of Features and Epics." ([PLAN])

> "Active backlogs: Select one or more active backlogs for a team. If you encounter issues selecting a backlog level, check the Team Backlog settings to ensure the backlog level is enabled for the team." ([PLAN2])

> "Product backlog items or portfolio backlogs defined and assigned to either a Start Date, End Date, or an Iteration Path." ([PLAN2], Prerequisites)

> Rollups: "Rollup views are available for Feature, Epic, or portfolio backlogs you add to your project." ([PLAN])

**Conclusion:** Delivery Plans are scoped to **product (Requirement) backlog + portfolio backlogs**. Requirement-level custom types **will** appear on plans (given they're selected on the team's backlog, and have dates/iteration). **Task/iteration-level items are never listed as selectable in either [PLAN] or [PLAN2]; the docs only ever name "product backlog or portfolio backlog".** The docs do not contain an explicit sentence saying "Tasks do not appear"; the exclusion is by the scoping sentence above. If you need certainty about Task-level items, that specific negative is **not documented explicitly; verify empirically**.

This is a real argument in favour of putting Construction/Validation/Documentation at **Requirement** level rather than Iteration level: Requirement level is plan-visible.

---

## Q6 — Can a WIT have no backlog level / behavior at all?

**Yes. That is the default for custom WITs.**

> "By default, custom work item types aren't added to any backlog." ([WIT])

> "(Optional) To add the work item type to a backlog, see Customize your backlogs or boards for a process." ([WIT])

> FAQ: "Q: How do I get my custom work item type to show up on my backlog? A: Modify your requirement backlog to include the custom work item type." ([WIT])

Unassigned WITs remain *offerable* to any level until assigned:

> "Each Edit backlog level dialog automatically includes inherited and custom work item types that aren't assigned to other backlog levels… These same WITs, along with any custom work item types, appear in the Edit backlog level dialog of all backlog levels, until they get assigned to a particular backlog level." ([BB])

A system precedent for "no level" also exists:

> "The Bug WIT doesn't belong to any specific backlog level by default. Each team can decide how they want to manage bugs." ([BB])

**Consequences of no level (documented):** the WIT does not appear on any backlog or board ([WIT] + [BB], by the sentences above), and the level-driven hidden fields (Stack Rank / Story Points / Remaining Work) are not injected, since injection is triggered by adding the WIT to a level ([BB]).

**Can a level-less custom type still be linked as a child?** The parent-child link type documentation places no backlog-level precondition on link creation ([LTR]). Microsoft's own Test artifacts are the clearest example of level-less types, and the docs note they are *not* linked hierarchically: *"You can't construct a query that shows a hierarchical view of Test Plans, Test Suites, and Test Cases. These items aren't linked together using Parent/Child or any other link type."* (https://learn.microsoft.com/en-us/azure/devops/boards/queries/link-work-items-support-traceability?view=azure-devops) — that is a statement about Test artifacts specifically, **not** a general rule that level-less types can't be parented.

**No primary source explicitly states "a work item type with no backlog behavior can still be a Parent/Child link target."** It is strongly implied by [LTR] listing no such restriction, but: **not documented; verify empirically** before relying on it.

---

## Q7 — What breaks when you change backlog behavior on a live process?

Documented warnings, verbatim:

- **Changes propagate immediately, org-wide.** "When you customize an inherited process, any projects that use the process automatically reflect the customizations. To ensure a smooth transition, we recommend that you create a test process and project to test your customizations before you implement them organization-wide." ([BB])
- **Deleting a backlog level destroys its boards and their customizations for every team.** "Deleting a backlog level removes the backlog and board associated with the level for all teams, including customizations made to them. The work items defined with the associated work item types aren't deleted or affected in any way." ([BB])
- **Only the top custom portfolio level can be deleted** — the supported operation is listed as "Delete the top-level custom portfolio backlog" ([BB], and [CP]).
- **Inherited levels cannot be removed.** "You can't remove an inherited portfolio level from a product. You can rename the level, or disable WITs…" ([BB])
- **Default inherited WITs can't be removed from a level, only disabled.** "You can't remove the default inherited work item type from any backlog level, but you can disable the corresponding WIT. For example, you can disable the User Story WIT for the Agile Requirement backlog as long as you added another work item type to support that backlog." ([BB]) Same note repeated for Epics/Features ([BB]), Requirements ([BB]), and Iteration ([BB]).
- **Disabling a WIT: existing data is untouched.** "Disabling a WIT removes the WIT from the New dropdown menu and add experiences. It also blocks creating a work item of that WIT type through REST APIs. No changes are made to existing work items of that type. You can update or delete them, and they continue to appear on backlogs and boards. Both work item types need to be enabled to perform a change type operation." ([WIT])
- **Destroying a WIT is irreversible.** "Destroying a WIT deletes all work items and data associated with that WIT, including historical values. Once destroyed, you can't recover the data." ([WIT])
- **Moving a WIT between levels can strand existing hierarchies.** Not stated as such in the docs, but the reorder-error behaviour in [REORD] is the mechanism: existing parent-child links that become same-category after a level change produce "Work item X can't be reordered because its parent is on the same category." ([REORD]) **The docs do not describe a migration or fix-up performed by ADO when a WIT's level changes — not documented; verify empirically.**
- **Effect on saved queries:** queries filter on fields and link types, not backlog levels; **the docs contain no statement about backlog-level changes affecting saved queries — not documented; verify empirically.**
- **Effect on existing Delivery Plans:** [PLAN2] says plans select "Active backlogs" for a team and warns "If you encounter issues selecting a backlog level, check the Team Backlog settings to ensure the backlog level is enabled for the team." ([PLAN2]) **What happens to a saved plan whose referenced level is deleted is not documented; verify empirically.**
- **Per-team enablement is a separate axis.** "Each team can select and configure the backlog levels that fit their needs." — Team settings → Backlogs → check the levels the team manages (https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels?view=azure-devops). A newly created level is not necessarily visible to every team until enabled.

---

## Recommendation implied by the evidence (clearly marked as our judgment, not documentation)

The following is **our inference**, not Microsoft documentation:

- The Hyperbright ask ("a unit of work that can be pull-requested into main") maps to the **Requirement backlog**, because Requirement level is the level whose injected field is *Story Points/Effort* — the estimation unit ([BB]) — and because Requirement level is Delivery-Plan-visible ([PLAN]).
- **Map** would sit at the **Features** level next to Feature (a WIT can be added to a level via the Edit backlog dialog, [BB]), giving Epic → (Map, Feature) → (Construction, Validation, Documentation, …) → Task **without creating any new behavior**. `customization` stays `inherited`, no `Custom.<guid>` behavior is created, blast radius is confined to the two Edit-backlog-level dialogs.
- Cost of that approach: Map and Feature are siblings, so Map cannot be a parent of Feature on the backlog UI without hitting the same-category reorder error ([REORD]). If Map must be *above* Feature, the only supported construction is a **new top-level portfolio backlog** — which puts Map above **Epic**, not between Epic and Feature ([BB]).
