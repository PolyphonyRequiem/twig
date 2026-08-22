# Azure DevOps Services: multi-level process inheritance — findings

Research date: 2026-08-22. Org under test: `PolyphonyRequiem`. All claims cited inline to Microsoft Learn primary sources; one claim is backed by a live API probe against the real organization and is labelled as such.

## DIRECT ANSWER

- **Multi-level process inheritance does NOT exist in Azure DevOps Services.** An inherited process must inherit from a *system* process. Confirmed empirically: `POST _apis/work/processes` with `parentProcessTypeId` = the custom "Hyperbright" process returns `VS402372: Inherited processes must inherit from a system process: Agile, Scrum, or CMMI.` (live probe, org `PolyphonyRequiem`, api-version 7.1, HTTP 500 / `ProcessInvalidParentException`). The prior is **CONFIRMED**.
- **"Create copy of process" produces an independent snapshot, not a link.** Docs direct you to make changes to the copy and then *migrate projects off the original and disable/delete the original* — i.e. copies are managed as replacements, never as live-linked siblings ([manage-process.md#copy-a-process](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#copy-a-process)). So three cloned processes = exactly the "mirror with no compiler" the team fears.
- **One process can be shared by many projects, and changes propagate instantly to every project using it**: "Changes you make to the inherited process automatically update all projects in the organization that use that process." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).
- **The mechanism that actually delivers the proposal is: ONE inherited process, MULTIPLE teams (in one or several projects), with per-team backlog-level visibility, area paths, board columns/swimlanes and working days.** "Each team can select and configure the backlog levels that fit their needs. For example, feature teams might focus on the product backlog, while management teams might enable both the feature and epic backlogs." ([select-backlog-navigation-levels.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels)) — this is precisely the "Human Devs / AI Agents / Human Oversight" split.
- **Confirmed split of responsibility:** work item types, fields, states, rules and *which portfolio backlog levels exist* are **process-level** (shared, single source of truth); which of those levels a team **sees**, plus area/iteration paths, board columns, swimlanes and working days, are **team-level** (independently variable). Limits: 256 processes/org, 1,000 projects/org, 5,000 teams/project; **no documented cap on projects per process**.

---

## 1. 🔴 THE CORE QUESTION — can an inherited process inherit from another inherited process?

**No. Inheritance depth is exactly one level: system process → inherited process. There is no grandchild.**

Documentation evidence:

- *Create and manage inherited processes* instructs you to pick the parent from the system processes only: "Choose the same system process that was used to create the project that you want to customize. The process types can include Agile, Basic, Scrum, and Capability Maturity Model Integration (CMMI)." ([manage-process.md#create-an-inherited-process](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#create-an-inherited-process)). The tutorial's stated outcome is likewise scoped: "Create an inherited process based on the Agile, Scrum, Basic, or CMMI models."
- *Process customization and inheritance* states: "*Inherited processes* are customized from system processes and inherit definitions from the system process they're based on." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).

**Caveat, stated honestly:** the same page contains one sentence that reads ambiguously — "Any updates Microsoft makes to system processes automatically update in inherited processes **and their child inherited processes**." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)); *manage-process.md* similarly says "Inherited child processes automatically update, based on their parent system processes." Read alone, "their child inherited processes" could be taken to imply a second level. It does not: "child inherited process" is the docs' term for the inherited process itself relative to its system parent. The prose is loose here, so prose alone is not decisive — which is why this was verified against the API.

**REST API contract.** `POST https://dev.azure.com/{organization}/_apis/work/processes?api-version=7.1` takes `parentProcessTypeId` — "The ID of the parent process". The reference **does not document any constraint** on which processes are legal parents; the sample uses the Agile system process id `adcc42ab-9882-485e-a3ed-7678f01f66bc` ([Processes - Create](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/processes/create?view=azure-devops-rest-7.1); identical in 7.2-preview.2). **The constraint is not documented in the REST reference; verified empirically instead.**

**Empirical verification (live, org `PolyphonyRequiem`, 2026-08-22):**

```
POST https://dev.azure.com/PolyphonyRequiem/_apis/work/processes?api-version=7.1
{"name":"ZZ-MultiLevel-Probe",
 "parentProcessTypeId":"ba4e268d-7d67-43bd-8065-df7ab52fba0c",   // Hyperbright (inherited)
 "description":"probe"}

HTTP 500
{"message":"VS402372: Inherited processes must inherit from a system process: Agile, Scrum, or CMMI. Choose one of these processes and try again.",
 "typeKey":"ProcessInvalidParentException","errorCode":402372}
```

That is the server enforcing exactly one level of inheritance. (The error text omits "Basic", but Basic is a valid parent per the create-process docs; the omission is a stale message string, not a restriction relevant here — Hyperbright itself descends from Basic.)

**Verdict: the proposal's "three processes inheriting from one shared custom parent" is impossible in Azure DevOps Services. No workaround, no preview flag documented.**

## 2. What does "Copy process" / clone actually do?

The copy is an **independent snapshot with no ongoing link to the original.** The docs never describe any propagation between an original process and its copy; the workflow they prescribe is explicitly a *replace-the-original* migration ([manage-process.md#copy-a-process](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#copy-a-process)):

> "Make your changes to the copied process. Because no project is currently using the new (copied) process, your changes don't affect any projects."
> "Roll out your updates by changing the process of the projects that need the new changes."
> "Disable or delete the original process."

And the stated purpose is to *break* the live-propagation behaviour of a shared process: "If you modify a process used by multiple projects, each project immediately reflects the incremental process change. To bundle process changes before rolling them out to all projects, complete the following procedure." (same section).

Whether a later change to the original propagates to the copy: **the docs never state it propagates, and the entire prescribed workflow presupposes it does not** (otherwise the "test on a copy without affecting projects" pattern would be incoherent). A copy retains the same *system* parent, so Microsoft's system-process updates continue to reach both copies independently. Beyond that, propagation from original→copy is **not documented as existing; treat as non-existent, and verify empirically if you need certainty.**

**Consequence for the team:** N copied processes are N independently drifting artifacts. Nothing in the platform reconciles them. This is the failure mode the team named.

## 3. Inheritance mechanics — what propagates, what's overridable, what's locked

- **System processes are immutable.** "The *system processes* Agile, Basic, Scrum, and Capability Maturity Model Integration (CMMI) are locked, and users can't change them. Microsoft owns these system processes and updates them periodically." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)). Also: "The default system processes are locked, so you can't customize them." ([manage-process.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process)).
- **Microsoft's updates flow down automatically.** "Any updates Microsoft makes to system processes automatically update in inherited processes and their child inherited processes." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).
- **A process change hits every project using it, immediately.** "Changes you make to the inherited process automatically update all projects in the organization that use that process." (same page) and "each project immediately reflects the incremental process change" ([manage-process.md#copy-a-process](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#copy-a-process)). *This is the only genuine "single source of truth" mechanism ADO offers.*
- **What an inherited process can override / add:** "Each inherited process inherits the WITs defined in the underlying Basic, Agile, Scrum, or CMMI system process… You can add fields and modify the workflow and work item form for all WITs that display on the **Work Item Types** page of an inherited process. You can also add custom WITs." Inherited WITs can be disabled rather than deleted: "If you don't want users to create new work items based on an inherited process WIT, you can disable it." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).
- **What is locked:** "Some options of inherited elements are locked and can't be customized" and "Locked fields and inherited fields correspond to fields from a system process" ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model); [customize-process.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process)). The exact per-element list of locked attributes is spread across the customize-* topics rather than enumerated in one table; **a single authoritative "locked attributes" list is not documented — verify per element empirically.**
- **Fields are org-wide, not process-scoped.** "Fields are defined for all projects and processes in an organization. … You can add any custom field you define for a WIT in one process to any WIT defined for another process." ([inheritance-process-model.md#field-customizations](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)). *This is the one thing that genuinely is shared across sibling processes — field definitions. WIT definitions, states, rules and layouts are not.*
- **The `customizationType` enum** on process objects is `System` / `Inherited` / `Custom`: "System behaviors are inherited from parent process but not modified. Inherited behaviors are modified behaviors that were inherited from parent process. Custom behaviors are behaviors created by user in current process." ([Processes - Create, Definitions](https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/processes/create?view=azure-devops-rest-7.1)). Note this is a two-generation model only.

## 4. Projects per process, and moving a project between processes

- **Two (or many) projects can share one process — this is the intended design.** "All projects in an organization that use the inherited process get the customizations you make to that process" and "All projects in an organization can share all of its processes. You customize the process instead of customizing the single projects." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).
- **A project can be moved to another process later**, either within the same base ("Switch within the same base process") or across models ("Migrate to a different process model … from Agile to Scrum or Basic to Agile") ([manage-process.md#migrate](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#migrate)).
- **Documented warnings / what breaks** ([manage-process.md#migrate](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#migrate)):
  > "You can change the process of a project as long as you don't have any undeleted work items of a custom work item type that isn't also defined in the target process."
  > "If you change a project to a system process or other inherited process that doesn't contain the same custom fields, data is still maintained. However, any custom fields not represented in the current process don't appear on the work item form. You can still access the field data by using a query or the REST APIs. These fields are locked from changes and appear as read-only values."
  > "When you switch a project to an inherited process, some Agile tools or work items might become invalid. For example: If you designate a field as required, work items that lack the field display an error message… If you add or modify workflow states for a work item type visible on your board, update the board column configurations for all teams within the project."

  And from [inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model): "When you transition a project to a different process, some existing tools or work items might become invalid… If the process change adds, removes, or hides workflow states for a WIT that appears on a board, ensure to update the board column configurations for all teams defined in the project."
- **Reversibility assessment:** moving a project between processes is a supported, documented operation and **field data is not destroyed**. The hard blocker is *work items of a custom WIT absent from the target process*. So a wrong choice is recoverable, but the cost scales with how many custom-WIT work items exist. Deciding early is cheap; deciding after Map/Grilling/Research/Spec items accumulate is expensive.

## 5. Alternatives for keeping several closely-related setups consistent

**(a) One process shared by several PROJECTS.** Fully supported and the documented intent: "All projects in an organization can share all of its processes" and process edits "automatically update all projects in the organization that use that process" ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)). Differentiation then happens per project via teams and area paths. Zero drift risk — there is literally one definition.

**(b) One process, several TEAMS in one project, each with its own tool configuration.** Also fully supported: "You can then configure project backlogs, sprints, and boards for each project team" ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)); "Each team you create gains access to a suite of Agile tools and team assets. These tools enable teams to work autonomously while collaborating with other teams across the enterprise. Each team can configure and customize these tools to support their unique workflows and processes." ([about-teams-and-settings.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings)).

What a team configures independently, per the team-configuration table ([boards/includes/team-configuration.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings), rendered in "Each team gets their own set of tools"):
- **Backlogs:** configure area paths; select active iteration paths (sprints); **select backlog levels**; show bugs on backlogs & boards.
- **Sprints/Scrum:** select active iteration paths (sprints); sprint capacity; taskboard; sprint burndown.
- **Boards:** Kanban board, Features board, Epics board, cumulative flow — configured per team.
- **Team defaults driving what appears:** "Work items that appear on team backlogs and boards are determined by the team's *area paths* and *iteration paths*." A team defines: selected area paths, default area path, selected iteration paths, backlog iteration path, default iteration path ([about-teams-and-settings.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings)).
- **Backlog level visibility explicitly:** "Each team can select and configure the backlog levels that fit their needs. For example, feature teams might focus on the product backlog, while management teams might enable both the feature and epic backlogs. Configure these backlog levels through team settings…" — and "This setting affects the backlog and board views for all team members" ([select-backlog-navigation-levels.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels)).
- **Board columns and swimlanes:** configured per team from the team's board settings ([select-backlog-navigation-levels.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels) shows the per-team **Configure team settings** gear; board column configuration is called out as per-team in the migration warning: "update the board column configurations for all teams within the project" — [manage-process.md#migrate](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#migrate)). **Working days / swimlanes: these are team settings in the same **Configure team settings** dialog, but I did not retrieve a sentence naming "working days" as team-scoped from a primary page in this pass — treat "working days is team-level" as not-yet-cited here; verify in team settings UI.**
- **Caution when splitting by area path:** "Avoid assigning the same area paths to more than one team." ([object-limits.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits)).

**(c) Several processes kept in sync by tooling/REST.** Possible but explicitly a bolt-on: Microsoft points to the community **process-migrator** tool for moving processes between organizations — "Use the [import/export process tool](https://github.com/Microsoft/process-migrator) to copy the process to the test organization." ([customize-process.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process)). There is **no documented first-party mechanism that keeps two sibling processes in sync inside one organization** — any such sync is code you own, with no platform-side consistency check. This is the "mirror with no compiler" shape and should be rejected on the team's own stated grounds.

## 6. 🔴 The mechanism that actually delivers the proposal

Since multi-level inheritance is impossible (§1), **one process + multiple teams with per-team backlog visibility is the correct mechanism**, and the stated prior about the process/team split is **CONFIRMED**:

**MUST be shared (process-level, single definition, one source of truth):**
- Work item types — inherited WITs plus every custom WIT (Map, Grilling, Research, Prototype, Spec, Decision, Idea, Wayfinder Task). "Each inherited process inherits the WITs defined in the underlying … system process… You can also add custom WITs." ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model))
- Fields, workflow states, work item form layout, rules — all customized on the process, and "Fields are defined for all projects and processes in an organization." (same page)
- **Which portfolio backlog levels EXIST** — adding a backlog level is a process customization ("**Inheritance**: [Customize your backlogs or boards for a process]" — [select-backlog-navigation-levels.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels)), capped at 5 portfolio backlog levels per process ([object-limits.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits)).
- Enabling/disabling a WIT — process-level, so you **cannot** hide a WIT from one team and show it to another via this switch ([inheritance-process-model.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model)).

**CAN differ per team (team-level):**
- **Which backlog levels are visible** — "Each team can select and configure the backlog levels that fit their needs." ([select-backlog-navigation-levels.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels)) → *this alone gives you "Human Devs see the task/story levels", "Human Oversight sees Epics/Features only".*
- Selected + default **area paths** and selected/backlog/default **iteration paths** — these determine which work items appear at all ([about-teams-and-settings.md](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings)).
- **Board columns** per team ([manage-process.md#migrate](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process#migrate) — "board column configurations for all teams within the project").
- Show-bugs-on-backlog toggle, sprint set, capacity, taskboard, dashboards ([team-configuration table](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings)).
- **Swimlanes and working days: believed team-level (both live in team/board settings) but not cited to a primary sentence in this pass — verify empirically before relying on it.**

**Correction to note:** a team **cannot** have a different *set of work item types* than another team on the same process. If the "Human Devs" and "AI Agents" workflows need genuinely different WITs (not just different visible backlog levels / different boards), one process cannot express that — and neither can multi-level inheritance, since it doesn't exist. The choice at that point is: (i) accept a superset of WITs on one shared process and differentiate by team/area path, or (ii) accept N cloned processes and own the drift. **Recommendation follows the team's own stated aversion: option (i).**

## 7. Limits

From [Work tracking, process, and project limits](https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits) (Azure DevOps Services / Inheritance model):

| Object | Limit |
|---|---|
| Processes per organization | **256** |
| Work item types per process | 64 |
| Fields per process | 1,024 |
| Portfolio backlog levels per process | 5 |
| Projects per organization | **1,000** ("Azure DevOps Services limits each organization to 1,000 projects. When you go beyond 300 projects, certain experiences … might degrade.") |
| Teams per project | **5,000** |
| Area paths per project / per team | 10,000 / 300 |
| Iteration paths per project / per team | 10,000 / 300 |
| Work items displayed per backlog | 10,000 |
| Team dashboards per team | 500 |
| Delivery plans per project | 1,500 |

**Max projects per process: not documented; no cap appears on the object-limits page. Effectively bounded by the 1,000-projects-per-organization limit — verify empirically if a hard number matters.**

---

## Sources

- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/manage-process (Tutorial: Create and manage inherited processes)
- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/inheritance-process-model (Process customization and inheritance)
- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/customize-process (Customize a project using an inherited process)
- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/about-teams-and-settings (About teams and Agile tools)
- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/select-backlog-navigation-levels (Select backlog navigation levels)
- https://learn.microsoft.com/en-us/azure/devops/organizations/settings/work/object-limits (Work tracking, process, and project limits)
- https://learn.microsoft.com/en-us/rest/api/azure/devops/processes/processes/create?view=azure-devops-rest-7.1 (Processes - Create)
- Live API probe, org `PolyphonyRequiem`, 2026-08-22 (error `VS402372` / `ProcessInvalidParentException`)
