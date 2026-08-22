# Azure DevOps: audience-targeted portfolio views and work abstraction/rollup

Research date: 2026-08-22. Scope: Azure DevOps Services (cloud). Every claim is cited to a Microsoft Learn URL. Quotes are taken from the primary markdown sources in `MicrosoftDocs/azure-devops-docs` (`main` branch), which are the sources that render to the cited Learn pages.

Where documentation does not answer a question, this file says **"not documented; verify empirically"** rather than inferring.

---

## DIRECT ANSWER

- **No.** A Delivery Plan cannot include a first-party "abstraction of other work" — a plan renders *actual work items* drawn from selected team backlog levels; there is no documented summary/virtual/aggregate card type. Plans are described as "a highly interactive calendar view of multiple team backlogs" (https://learn.microsoft.com/azure/devops/boards/plans/add-edit-delivery-plan).
- **The documented abstraction mechanism is the backlog-level hierarchy plus computed rollup, not a separate artifact.** Rollup "automatically sums child work item values to display totals on parent items" and is computed by the Analytics service — nobody maintains the number (https://learn.microsoft.com/azure/devops/boards/backlogs/display-rollup).
- **Audience targeting is achieved by creating multiple plans over the same underlying work**, each with a different set of teams/backlog levels, field criteria, card fields, styles, markers and tag colors. A project may have up to **1,500 delivery plans** (https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits).
- **Granularity per audience is chosen by which backlog level(s) a plan (or a team) shows.** Microsoft explicitly recommends limiting backlog levels per team — "Epics for management teams, Features and Stories for feature teams" (https://learn.microsoft.com/azure/devops/boards/plans/visibility-across-teams).
- **Microsoft's documented recommendation for showing different audiences different granularity is: hierarchical teams + area paths + backlog levels + rollup columns + Delivery Plans + dashboards.** No Microsoft documentation reviewed here recommends creating extra work item types for reporting or summary purposes (https://learn.microsoft.com/azure/devops/boards/best-practices-agile-project-management, https://learn.microsoft.com/azure/devops/boards/plans/portfolio-management).
- **For a view with *no* new work items at all, use shared queries + query charts, dashboard widgets, and Analytics views/Power BI** — all of these read existing items (https://learn.microsoft.com/azure/devops/report/dashboards/charts, https://learn.microsoft.com/azure/devops/report/dashboards/widget-catalog, https://learn.microsoft.com/azure/devops/report/powerbi/what-are-analytics-views).

---

## 1. Delivery Plans — audience targeting

Source: https://learn.microsoft.com/azure/devops/boards/plans/add-edit-delivery-plan and https://learn.microsoft.com/azure/devops/boards/plans/review-team-plans

### 1.1 The settings list, quoted verbatim

From the "Plan customization options" table (add-edit-delivery-plan):

> |Page | Use to... |
> |**Overview**|Edit the plan **Name** or **Description**. |
> |**Teams** |Add or remove a team backlog. You can add up to 20 backlog levels for Azure DevOps Services or 15 backlog levels for Azure DevOps Server 2022. You can add a mix of backlog levels and teams from any project defined for the organization. |
> |**Field criteria**|Specify field criteria to filter work item types displayed on the plan. The system evaluates all criteria as an AND statement. If no fields are specified, then all work item types that appear on the teams backlog level appear on the plan. |
> |**Markers** |Add up to 30 milestone markers to the plan. Specify a label and select a color. |
> |**Fields** |Add or remove fields from cards to display on the plan, similar to how you customize them for your board. You can't add rich-text (HTML) fields, such as the Description field, to a card even if it appears in the list. These field types present too many challenges to format on a card. |
> |**Styles** |Add styling rules to change card color based on field criteria. |
> |**Tag colors**|Add tags and specify a tag color. Optionally enable or disable a tag color. |

Additional documented per-plan/per-view settings:

- **Styles limit**: "You can specify up to 10 styles." (add-edit-delivery-plan)
- **Styles restriction**: "You can't directly select **Title**, **Description**, and other rich-text fields, such as **Assigned To**. Even if you can select a field, you might not be able to specify a value or the specific value you want. For example, you can't specify **Tags** that are either *Empty* or *Not Empty*." (add-edit-delivery-plan)
- **Iteration-path styling**: styles can use the `@CurrentIteration` macro for a chosen team (add-edit-delivery-plan, "Set color for an Iteration Path"; Azure DevOps Services only).
- **Card field tip**: "To show the **Title** of the parent work item, choose the **Parent** field... You can filter your plan based on parent work items, whether you add the **Parent** field to cards or not." (add-edit-delivery-plan) — this is the closest documented thing to showing parent-level abstraction on a child card.
- **Rollup toggle**: "To display rollups, select **Settings** > **Fields**, and then select **Show child rollup data**. Rollups aren't supported for child work items that belong to different projects than the originating parent work item." (review-team-plans)
- **Dependency lines**: view options let you "Show and hide dependencies between work items." Dependencies require Predecessor/Successor or custom dependency links; "Remote link types aren't supported, and you can use custom link types only in on-premises environments." (review-team-plans)
- **Collapse/expand**: "To expand or collapse all team backlog rows, select the arrow next to **Teams** on the top bar. To expand or collapse individual rows, select the arrow next to each team name." and "Use the **Expand or collapse cards** icon to toggle between showing only titles in cards or displaying all the fields configured for the plan." (review-team-plans)
- **Collapsed row semantics** — the only built-in "summary" affordance: "A collapsed row shows a summary of backlog items. An expanded row shows cards for each backlog item." and "To focus on a summary view of scheduled work, collapse all teams by selecting the expand/collapse icon next to **Teams** on the top bar." (review-team-plans)
- **Interactive filter (not persisted per plan; view-time)**: "Select the **Filter** icon to display the filter toolbar and filter the plan view. You can filter on any field included in the plan, or by keyword or text filter." (review-team-plans)
- **Favorite / Fullscreen** toggles (review-team-plans).

### 1.2 Counts and limits

- **Plans per project: 1,500.** Table row: `| Delivery plans per project | 1,500 |` (https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits). A per-*organization* plan limit is **not documented; verify empirically**.
- **Teams per plan.** The docs are internally inconsistent and both statements are reproduced here rather than reconciled by inference:
  - add-edit-delivery-plan (Add a plan): "**Team selection:** You can choose one or more teams from any project defined in the organization or collection. You can select up to a maximum of 15 teams."
  - add-edit-delivery-plan (Teams tab): "You can add up to 20 backlog levels for Azure DevOps Services or 15 backlog levels for Azure DevOps Server 2022."
  - review-team-plans (prerequisites): "Plan views are limited to a maximum of 20 teams or backlogs."
  The effective rule (teams vs. team×backlog-level rows) is **not documented unambiguously; verify empirically**.
- **Markers per plan: up to 30.** "You can add up to 30 markers. After 30 markers, the system disables the **+ Add marker** button." (add-edit-delivery-plan)
- **Styling rules per plan: up to 10** (add-edit-delivery-plan).

### 1.3 Can two plans show the same work at different granularity for different audiences?

Yes, and this is documented in terms of mechanism rather than as an explicit "audience" feature:

- Rows are chosen per team *and* per backlog level: "**Active backlogs:** Select one or more active backlogs for a team." (add-edit-delivery-plan). So Plan A can select only the top portfolio level for the same team whose Requirement level Plan B selects.
- Field criteria narrow what appears: "For example, to exclude bugs from the view, add the following criteria: `Work Item Type <> Bug`." and the editing example "we add the **Tags** to the **Field criteria**. Only work items that contain the *Build 2021* tag appear in the Delivery Plan." (add-edit-delivery-plan)
- Card fields and styles are per plan (add-edit-delivery-plan), so the same item can render minimally in one plan and in detail in another.
- Plans are independently permissioned: "To manage permissions, edit, or delete a plan: Creator of the plan, or member of the **Project Administrators**, **Project Collection Administrators** group, or explicit permission granted through the plan's Security dialog." (add-edit-delivery-plan). Note *viewing*: "To view a Delivery Plan: Member of the **Project Collection Valid Users** group."

**Constraint on what can appear at all**: "Work items belong to the team's product backlog or portfolio backlog. Only work item types selected for viewing on a team's backlog appear on the plan." and items need "**Iteration Paths**, **Start**, and **End Dates** assigned, otherwise they don't appear on the plan." (review-team-plans / add-edit-delivery-plan prerequisites).

---

## 2. Rollup — automatic vs manual

Source: https://learn.microsoft.com/azure/devops/boards/backlogs/display-rollup

### 2.1 It is computed, not maintained

> "Rollup automatically sums child work item values to display totals on parent items. Use it to track work estimates, effort, size, or story points across your backlog hierarchy."

> "The Analytics service calculates rollup data."

There is **no documented field that a human sets to hold a rollup value**; rollup is a *column option on a view*, added via "**Column options** > **Add a rollup column** > **From quick list**". "Column options are user-specific and persist for each backlog." (display-rollup). So the rollup is a rendering of the children, not a stored duplicate — there is no copy that can be edited out of agreement.

### 2.2 (a) Rollup columns on backlogs

> "Rollup supports progress bars, work item counts, and numeric field sums for descendant work items within the same project."

> "Rollup requires parent-child links in the backlog hierarchy; test case links aren't included in rollup calculations."

> "Support for specific numeric fields, such as Effort, Story Points, or Size, depends on the selected backlog level and process configuration."

Example semantics: "**Progress by Work Items** shows progress bars based on the percentage of closed descendant items. For Epics, this includes all child Features and their descendants. For Features, this includes all child User Stories and their descendants." Counts roll transitively: "In this example, **Count of Tasks** is 2 and 4 for the parent user stories, and 6 for the parent Feature and Epic." Sums: "Add **Remaining Work of Tasks** to show the sum of **Remaining Work** across linked child tasks."

Where rollup can be viewed: product/portfolio backlogs, the sprint planning pane, sprint backlog and taskboard (display-rollup, "Get rollup data").

Custom types/fields are supported: "If you add a custom work item type or field to a backlog level, you can view rollup data based on those options... **Column options** > **Add a rollup column** > **Configure custom rollup**." (display-rollup)

### 2.3 (b) Rollup in Delivery Plans

> "Rollup progress is available in Delivery Plans." (display-rollup, Azure DevOps Services moniker)

> "A rollup provides a comprehensive view of child work item progress on a parent card in your delivery plan. Rollup views are available for Feature, Epic, or portfolio backlogs you add to your project." (https://learn.microsoft.com/azure/devops/boards/plans/review-team-plans)

Cross-project caveat, stated twice: "In Delivery Plans, child items from other projects aren't included in rollup calculations." (display-rollup) / "Rollups aren't supported for child work items that belong to different projects than the originating parent work item." (review-team-plans)

### 2.4 (c) Available aggregations

Documented aggregation kinds: **progress bars (percent of closed descendants), counts of descendant items, and sums of numeric fields** (display-rollup, IMPORTANT block above). Named examples in the docs: Count of Tasks, Remaining Work of Tasks, Count of Customer Requests, Effort / Story Points / Size sums. "Available options vary by: Process type; Backlog level; Whether **Show parents** is enabled." A complete enumerated menu of quick-list options is **not documented; verify empirically**.

### 2.5 Can a parent's rolled-up value drift from its children?

The docs describe **staleness and omission**, not stored divergence:

| Symptom (quoted from display-rollup troubleshooting table) | Cause quoted |
|---|---|
| "Rollup column shows blank values" | "Parent-child links are missing or selected field isn't available on child work item types" |
| "Rollup values don't match expected totals" | "Links include unsupported scenarios (for example, test case links) or hierarchy is incomplete" |
| "Rollup values appear stale" | "Analytics processing delay" |
| "Cross-project totals are missing in Delivery Plans" | "Delivery Plans rollup doesn't include child items from other projects" |

Also: "Large datasets can cause temporary display latency... If data isn't ready, the info icon can appear and some rows might be empty. After Analytics finishes processing recent changes, rollup columns refresh automatically."

So: the documented drift modes are **eventual-consistency lag** and **scope exclusions** (cross-project children, test-case links, missing links). There is **no documented case of a rollup value that a user can set and that then persists in disagreement with children** — rollup has no writable storage in the documented model. A caveat that *is* a stored-value hazard, and is documented, is the separate Remaining Work behavior: "When you close a task, the Remaining Work field automatically sets to zero." (display-rollup)

---

## 3. Does ADO have a "summary"/"abstraction" work item concept?

**No first-party summary/abstraction work item type is documented.** Findings:

- **Portfolio backlog semantics are parenthood, not abstraction.** "Portfolio backlogs let you add and group items into a hierarchy. You can also drill up or down within the hierarchy, reorder and reparent items, and filter hierarchical views." (https://learn.microsoft.com/azure/devops/boards/backlogs/define-features-epics). Epics/Features are ordinary work items that *are parents of* their children; nothing in the docs describes a work item that references a set it is not the parent of.
- **The Feature/Epic model is exactly the hierarchy + rollup model** described in sections 1–2: "Use **Epic** to represent large initiatives that span multiple features or releases." (https://learn.microsoft.com/azure/devops/boards/best-practices-agile-project-management)
- **OKR / "Objectives" support**: a full-text search of the `azure-devops-docs` repository markdown fetched for this research returned **no** occurrences of "OKR" or "Objectives and Key Results". First-party OKR/Objectives work item support is **not documented; verify empirically** (and there is no Microsoft Learn page for it that this research located).
- **"Summary work item" guidance**: no Microsoft Learn guidance using that concept was located in the portfolio-management, define-features-epics, best-practices, visibility-across-teams, or Delivery Plans articles. Whether such guidance exists elsewhere is **not documented in the sources reviewed; verify empirically**.
- The nearest documented abstractions are all **view-level, not item-level**: the collapsed Delivery Plan row ("A collapsed row shows a summary of backlog items", review-team-plans), rollup columns, query charts, and dashboard widgets.

---

## 4. Query-based and chart-based alternatives to a summary work item

### 4.1 Shared queries and query charts

- Query types: flat list, "Tree of Work Items", and "Work items and direct links" (https://learn.microsoft.com/azure/devops/boards/queries/using-queries). Tree queries: "Use the **Tree of Work Items** query to view a multi-tiered, nested list of work items. For example, you can view all backlog items and their linked tasks."
- Cross-project: the query editor supports a "Query across projects" option (using-queries).
- Macros include "@CurrentIteration, @CurrentIteration +/-n" (using-queries; detail at https://learn.microsoft.com/azure/devops/boards/queries/query-by-date-or-current-iteration).
- Charts: "Chart the results of a flat-list query to quickly view the status of work in progress. You can create pie, column, bar, pivot, trend, or burndown charts that show a count of work items or a sum of numeric fields like Story Points, Effort, or Remaining Work — grouped by State, Assigned To, or any other system or custom field." (https://learn.microsoft.com/azure/devops/report/dashboards/charts)
- Chart constraint: "Only flat-list queries support charts." and "To add a chart to a dashboard, save the query to a **Shared Queries** folder. Charts from **My Queries** are only visible to you."
- Cross-team slicing without new items: "use a stacked bar chart for work item count by **Node Name** (team) and **State**." (charts) — `Node Name` is also the backlog column that "shows the team name assigned to each work item" (https://learn.microsoft.com/azure/devops/boards/plans/portfolio-management).

### 4.2 Dashboards and widgets

Widget catalog: https://learn.microsoft.com/azure/devops/report/dashboards/widget-catalog

- Scope annotation used by the catalog: "**Project**: Widget where you can select the project and team when configuring the widget".
- Widgets documented as aggregating across **multiple teams**:
  - **Burndown**: "Displays a burndown chart that you can configure to span one or more teams, work item types, and time period. With it, you can create a release burndown, sprint burndown, or any burndown that spans teams and sprints."
  - **Burnup**: "Displays a burnup chart that you can configure to span one or more teams, work item types, and time period."
- Widgets that aggregate via a **shared query** (and therefore inherit whatever cross-team/cross-project scope the query has):
  - **Chart for Work Items**: "Displays a progress or trend chart that builds off a shared work item query."
  - **Query Results**: "A configurable tile that lists the results of a shared query."
  - **Query Tile**: "A configurable tile to display the summary of shared query results... You can optionally specify rules to change the query tile color based on the number of work items returned by the query."
- Single-team-scoped widgets per the catalog annotations: Cumulative Flow Diagram (Team), Cycle Time (Team), Lead Time (Team), Sprint Burndown (Team), Sprint Capacity (Team), Sprint Overview (Team), Velocity (Team).
- Permission caveat: "Data displayed within a chart or widget is subject to permissions granted to the signed in user."
- Limits: "Project dashboards per project | 500" and "Team dashboards per team | 500" (https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits).
- Whether any *first-party* widget aggregates across **multiple projects** in one widget instance is **not documented in the widget catalog; verify empirically** (the catalog documents team-spanning for Burndown/Burnup and a project selector for "Project"-scoped widgets, but does not state multi-project aggregation).

### 4.3 Analytics views / Power BI

Source: https://learn.microsoft.com/azure/devops/report/powerbi/what-are-analytics-views

> "An *Analytics view* provides a simplified way to specify the filter criteria for a Power BI report based on Analytics data. Analytics views support Azure Boards data. Each view corresponds to a flat list of work items. **Work item hierarchies aren't supported.**"

> "The default Analytics views return all the specified data in a project. They work well for customers with smaller datasets. For larger datasets, the amount of data generated by a default view might be too large for Power BI to load."

Custom views let you "fine-tune the records, fields, and history loaded into Power BI." Note the hierarchy limitation above: Analytics views are not a rollup mechanism themselves.

### 4.4 Which of these need no new work items?

**All of them.** Shared queries, query charts, dashboards/widgets, Analytics views/Power BI, rollup columns, and Delivery Plans all read existing work items and create no artifacts in the work item store. The only documented artifacts they create are *view objects* (queries, charts, dashboards, plans, views), which cannot hold a state that disagrees with the items — they are recomputed on read. (Sources as cited above; explicit "no new work items" phrasing is **not documented; this statement is a description of what each cited feature is defined to do — it reads work items and defines a view — not a quoted claim**.)

---

## 5. Microsoft's documented guidance / best practice

### 5.1 Per-audience granularity: limit backlog levels per team

https://learn.microsoft.com/azure/devops/boards/plans/visibility-across-teams:

> "Management teams define Delivery Plans to view scheduled deliverables across teams."
> "Management teams use portfolio backlogs to view feature teams under their area path."
> "Management teams create dashboards that monitor status, progress, and trends across teams."

> "Add a management team for a group of feature teams; management teams own Epics and enable only the Epic portfolio backlog level."

> "Limit backlog levels per team—Epics for management teams, Features and Stories for feature teams—to help teams focus on their responsibilities."

> "Management teams can drill down in a portfolio backlog to see Epic progress and the child backlog items that other teams own."

Documented limitation of this approach: "Although management teams can monitor Feature progress by enabling the Features backlog, multi-team board views have limitations. Even when teams configure identical board column mappings, updating Features on one team's board doesn't reflect on another team's board until the work item's state actually changes. Only a state change synchronizes column placement across boards."

### 5.2 Portfolio management

https://learn.microsoft.com/azure/devops/boards/plans/portfolio-management:

> "Portfolio backlogs let product owners track the work of multiple agile feature teams, monitor progress across projects, and manage risks and dependencies. Product owners create their vision and roadmap for each release and define high-level goals as Epics or Features. Feature teams break down the Epics or Features into Stories for prioritization and development."

> "To visualize ownership and progress involving other feature teams: Configure your backlog to show parent epics or features owned by other teams. Create queries to include work items from other teams. Add these queries to your team's dashboard for better visibility. Use the Delivery Plans feature in Azure Boards to get cross-team visibility into work items across multiple teams."

> "To view feature progress based on linked requirements, add a rollup column or view a delivery plan."

> "If you need more than three backlog levels, add them." (→ https://learn.microsoft.com/azure/devops/organizations/settings/work/customize-process-backlogs-boards)

### 5.3 Best practices for Agile project management

https://learn.microsoft.com/azure/devops/boards/best-practices-agile-project-management:

> "Define a team for each delivery group that works autonomously. Configure teams along value streams so each team can plan, track, and deliver independently while still rolling up to product-level roadmaps."

> "Give each team its own area path and iteration cadence."

> "Pick the work item types that match how your teams plan and deliver work. Map product-level work (epics and features) to team-level work (requirements) and optionally let teams break work into tasks."

> "Use **Epic** to represent large initiatives that span multiple features or releases."

> "Use the Features board, rollup columns on the Features backlog, and Delivery Plans to review progress across teams."
> Recommendations: "Add rollup progress or totals to the Features backlog to monitor completion at a glance." / "Customize Features board columns to match your delivery lifecycle (for example: Research, On Deck, In Progress, Customer Rollout)." / "Use Delivery Plans to coordinate cross-team dates and dependencies."

Dependencies guidance: "Track cross-team dependencies by using **Predecessor/Successor** links and by surfacing dependencies in Delivery Plans." with the recommendation "Tag dependent work with a consistent tag (for example, `dependency`) for quick queries."

### 5.4 Does Microsoft recommend extra work item types for reporting/summary?

**No such recommendation appears in any of the articles reviewed** (portfolio-management, visibility-across-teams, best-practices-agile-project-management, define-features-epics, review-team-plans, add-edit-delivery-plan, display-rollup). The consistent recommendation is: **hierarchical teams + area paths + limited backlog levels per team + rollup columns + Delivery Plans + dashboards/queries.** Work item type customization is discussed in the docs as a way to add types *to a backlog level* for tracking real work (e.g. "if you add the Customer Request type to the Requirements category", display-rollup; "we renamed the backlog, added **Customer Ticket** and **Issue**", customize-process-backlogs-boards) — not as a reporting device. An explicit Microsoft statement *against* summary work item types is **not documented; verify empirically**.

---

## 6. Tags, area paths, and board-level segmentation as audience tools

### 6.1 Area paths (and teams)

- Definition: "Area paths group work items by team, product, or feature area. Iteration paths group work into sprints, milestones, or other time-related periods. Both fields support hierarchical paths." (https://learn.microsoft.com/azure/devops/organizations/settings/about-areas-iterations)
- Area path is the primary team-scoping mechanism: team pages "show work relevant only to that team, based on assignments made to the work item area and iteration paths." (https://learn.microsoft.com/azure/devops/boards/plans/portfolio-management)
- Assigning work to an audience is an area-path edit: "product owners and development leads can review the backlog and assign specific items to various teams by setting the feature team **Area path**." They "can select multiple work items and bulk modify the area path." (portfolio-management)
- **Documented hazard of using area paths for overlapping audiences**: "You can assign the same **Area Path** to more than one team, but this can cause problems if two teams claim ownership over the same set of work items." (about-areas-iterations) and "Avoid assigning the same area paths to more than one team." (https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits). Delivery Plans repeats this: "Eliminate cross-team ownership of area paths to avoid undesirable edge cases." (review-team-plans)
- **Limits** (https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits):
  - Area paths per project: 10,000
  - Area paths per team: 300
  - Area path depth: **14 levels**
  - Iteration paths per project: 10,000; per team: 300; depth: 14 levels
  - Teams per project: 5,000

### 6.2 Tags

- Purpose: "Work item tags consist of one or two keywords that filter or define work tracking tools such as backlogs, boards, and queries." (https://learn.microsoft.com/azure/devops/organizations/settings/naming-restrictions)
- Tags are usable as an audience slicer in Delivery Plans in three documented ways: as **field criteria** ("Only work items that contain the *Build 2021* tag appear in the Delivery Plan"), as **card styling** ("we highlight the card based on its **Tags** assignment"), and as **Tag colors** (all: add-edit-delivery-plan).
- Best-practice usage: "Tag dependent work with a consistent tag (for example, `dependency`) for quick queries." (best-practices-agile-project-management)
- **Limits** (https://learn.microsoft.com/azure/devops/boards/queries/add-tags-to-work-items and object-limits):
  > "While no hard limit exists, creating more than 100,000 tags for a project collection can negatively affect performance. Also, the autocomplete dropdown menu for the tag control displays a maximum of 200 tags."
  > "You can't assign more than 100 tags to a work item..."
  > "Limit queries to fewer than 25 tags. More than that amount and the query likely times out."
  - object-limits table: "Work item tags per work item | 100"; "Work item tags per organization or collection | 150,000".
  - Note the two sources give **100,000 (performance caution)** and **150,000 (stated limit)** for collection-level tag counts. The reconciliation is **not documented; verify empirically**.
- Tag styling caveat in plans: "you can't specify **Tags** that are either *Empty* or *Not Empty*." (add-edit-delivery-plan)

### 6.3 Board-level segmentation

- A board is per team per backlog level; column customization is recommended to match the audience's lifecycle: "Customize Features board columns to match your delivery lifecycle (for example: Research, On Deck, In Progress, Customer Rollout)." (best-practices-agile-project-management)
- **Documented failure mode when two teams' boards cover the same items**: "Even when teams configure identical board column mappings, updating Features on one team's board doesn't reflect on another team's board until the work item's state actually changes. Only a state change synchronizes column placement across boards." (visibility-across-teams). This is the one documented case in this research where two *views* of the same item can display disagreeing positions — because board column is per-team state stored per board, not derived. Contrast with rollup (section 2), which is derived and cannot be edited into disagreement.
- Board display limits: "Boards | 1,000 cards excluding cards in the **Proposed** and **Completed** state categories"; "Backlogs | 10,000 displayed work items" (object-limits).

### 6.4 Which slicer for which audience purpose?

Microsoft does not publish a single comparison table of area paths vs. tags vs. teams for audience targeting; **an explicit "use X not Y for audiences" recommendation is not documented; verify empirically.** What *is* documented, from the citations above: area path drives team ownership and backlog membership (with an explicit warning against overlapping ownership), tags are a lightweight, non-exclusive cross-cutting label usable in plan field criteria/styles/colors, and teams determine which backlog levels and boards an audience sees.

---

## Sources

- Add or edit a Delivery Plan — https://learn.microsoft.com/azure/devops/boards/plans/add-edit-delivery-plan
- Review team Delivery Plans — https://learn.microsoft.com/azure/devops/boards/plans/review-team-plans
- Display rollup progress or totals — https://learn.microsoft.com/azure/devops/boards/backlogs/display-rollup
- Manage product and portfolio backlogs — https://learn.microsoft.com/azure/devops/boards/plans/portfolio-management
- Manage priorities and gain visibility across teams — https://learn.microsoft.com/azure/devops/boards/plans/visibility-across-teams
- Define features and epics — https://learn.microsoft.com/azure/devops/boards/backlogs/define-features-epics
- Best practices for Agile project management — https://learn.microsoft.com/azure/devops/boards/best-practices-agile-project-management
- Use the query editor / using queries — https://learn.microsoft.com/azure/devops/boards/queries/using-queries
- Track progress with status and trend query-based charts — https://learn.microsoft.com/azure/devops/report/dashboards/charts
- Widget catalog — https://learn.microsoft.com/azure/devops/report/dashboards/widget-catalog
- What are Analytics views — https://learn.microsoft.com/azure/devops/report/powerbi/what-are-analytics-views
- Add work item tags — https://learn.microsoft.com/azure/devops/boards/queries/add-tags-to-work-items
- About area and iteration paths — https://learn.microsoft.com/azure/devops/organizations/settings/about-areas-iterations
- Work tracking, process, and project limits — https://learn.microsoft.com/azure/devops/organizations/settings/work/object-limits
- Naming restrictions — https://learn.microsoft.com/azure/devops/organizations/settings/naming-restrictions
- Customize backlogs and boards (Inheritance) — https://learn.microsoft.com/azure/devops/organizations/settings/work/customize-process-backlogs-boards
