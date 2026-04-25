# Twig Conceptual Scenario Map

Comprehensive scenario landscape for Twig as an offline console work item management tool.

## 1. Bootstrapping & Workspace Setup
- Initialize a new workspace from a remote project (org, project, auth)
- Re-initialize when switching projects or teams
- Clone a workspace onto a second machine (portable `.twig/` directory)
- Work without ever initializing — create local-only items that sync later
- Multi-project — manage items across multiple ADO projects in one workspace

## 2. Sync & Connectivity
- Pull latest — refresh local cache from remote
- Push local changes — send edits, notes, state transitions upstream
- Selective sync — only pull items in my sprint, or items I own
- Background auto-sync — periodic refresh without explicit command
- Sync on demand — user controls exactly when data flows
- Stale data awareness — know how old your local data is
- Offline-first — every read operation works without network
- Sync resumption — pick up where you left off after network outage
- Sync scope — sync a subtree, a query, or the whole project

## 3. Navigation & Discovery
- Browse my assigned work — what's on my plate right now
- Browse team's work — what's the team doing this sprint
- Navigate the hierarchy — drill into epics → features → stories → tasks
- Jump to a known item by ID — I know the number, take me there
- Search by keyword — find items by title, description content
- Filter by state — show me active/closed/new items
- Filter by type — show me only bugs, or only tasks
- Filter by tag — items with specific labels
- Filter by area path — items in a specific component area
- Filter by iteration — items in a specific sprint
- Saved queries — reusable filter combinations
- Recent items — what did I look at last?
- Bookmarks / favorites — items I want quick access to

## 4. Reading & Viewing Work Items
- View a single item in detail — all fields, description, acceptance criteria
- View item summary — compact one-line view (ID, type, title, state)
- View item in context — see where it fits in the hierarchy (parents, children, siblings)
- View description / rich text — render markdown or HTML description in the terminal
- View attachments list — know what files are attached
- View comments / discussion — read the conversation thread
- View history / changelog — what changed, when, by whom
- View links & dependencies — related items, predecessors, successors
- View tags — labels on the item
- View custom fields — process-specific fields beyond the standard set
- Diff against remote — what's different between my local and the server

## 5. Editing & Mutation
- Change title — rename a work item
- Change state — transition (New → Active → Resolved → Closed)
- Change assignment — assign to self, to someone else, or unassign
- Change iteration — move to a different sprint
- Change area path — reassign to a different team/component
- Edit description — modify the rich text body (in an editor)
- Edit acceptance criteria — modify the AC field
- Add/remove tags — label management
- Change priority / severity / effort — numeric/enumerated fields
- Edit custom fields — process-specific fields
- Batch edit — change a field across multiple items at once
- Stage changes — accumulate edits locally before pushing (git-style staging)
- Discard local changes — revert to remote state
- Review pending changes — see what I've modified before pushing

## 6. State & Workflow
- Transition with validation — only allow valid state transitions
- Shorthand transitions — `twig done`, `twig start`, `twig close`
- Transition with reason — some states require a reason field
- Transition with side effects — closing a parent when all children are done
- Kanban-style column move — drag/think in terms of board columns
- State history — when did this item enter each state

## 7. Creation & Templates
- Create a new work item — specify type, title, and optional fields
- Create from template — predefined field values for common patterns
- Create child — add a task under the current story
- Create sibling — add a peer item at the same level
- Clone an item — duplicate with modifications
- Quick capture — rapid item creation with minimal fields (just title + type)

## 8. Notes & Comments
- Add a comment — append to the discussion thread
- Add a local note — personal annotation not pushed to ADO
- View discussion — see comment thread chronologically
- Reply to a comment — threaded discussion
- Mention someone — @-mention in a comment

## 9. Conflict Resolution
- Detect conflicts — local edit vs. remote edit on the same field
- Interactive resolve — choose local, remote, or merge
- Auto-resolve trivial conflicts — non-overlapping field changes
- Conflict preview — show what would conflict before pushing
- Force push — overwrite remote (with confirmation)
- Force pull — discard local changes and accept remote

## 10. Hierarchy & Relationships
- View parent chain — walk up the tree
- View children — see all items underneath
- Reparent an item — move under a different parent
- Create a link — related, predecessor, successor, duplicate
- Remove a link — unlink items
- Dependency graph — what blocks what
- Orphan detection — items with no parent that should have one

## 11. Sprint & Iteration Management
- View current sprint — what's in the active iteration
- View sprint progress — completion %, burndown feel
- Move items between sprints — replan work
- Sprint retrospective view — what got done, what rolled over
- Backlog grooming — prioritize and estimate unscheduled items

## 12. Team & Collaboration
- View team members' work — who's working on what
- Assignment suggestions — who's underloaded
- Handoff — reassign with a note
- Standup view — yesterday/today/blockers per person
- Workload view — item count / effort by assignee

## 13. Shell & Environment Integration
- Git branch integration — associate current branch with a work item
- Auto-detect context from branch name — `feature/12345-foo` → item #12345
- Commit message integration — include work item ID in commits
- Shell prompt integration — show active item in PS1/Oh My Posh
- Pipe-friendly output — JSON, minimal, TSV for scripting
- Editor integration — open description in `$EDITOR`
- Clipboard — copy item URL or ID to clipboard
- Deep links — open item in browser

## 14. Configuration & Personalization
- Output format preference — human, JSON, minimal as default
- Color customization — type colors, state colors, theme
- Icon mode — unicode, nerd font, ASCII, none
- Field display preferences — which fields to show in list views
- Alias/shortcut commands — user-defined command aliases
- Default filters — always filter to my items unless `--all`
- Staleness threshold — how old before warning

## 15. Reporting & Analytics
- Status summary — counts by state, type, assignee
- Velocity — items completed per sprint
- Cycle time — how long items stay in each state
- Aging — how long items have been in current state
- Activity log — recent changes across the project
- Export — dump to CSV, markdown table, or clipboard

## 16. Bulk & Power Operations
- Query with WIQL — run arbitrary queries
- Bulk state transition — close all tasks under a completed story
- Bulk reassign — move all my items to someone else
- Import from file — create items from a CSV or markdown list
- Archive / cleanup — identify and close stale items

## 17. Offline Resilience
- Queue operations — edits queued when offline, pushed when online
- Operation log — see what's queued for sync
- Partial sync — push some changes, hold others back
- Conflict-free merge — idempotent operations that always resolve cleanly
- Offline duration awareness — how long since last sync, risk of conflicts

## 18. Interactive / TUI Mode
- Full-screen dashboard — tree + detail split view
- Keyboard-driven navigation — vim/arrow/tab
- Inline editing — edit fields in place
- Context switching — navigate to different items without leaving TUI
- Live refresh — auto-update when data changes
- Multiple views — switch between tree/board/list presentations

## 19. Security & Privacy
- Credential management — PAT, az cli, managed identity
- Credential rotation — re-auth when tokens expire
- Local data encryption — encrypt the SQLite cache at rest
- Audit trail — who changed what locally

## 20. Multi-tenancy & Scale
- Multiple organizations — work across different ADO orgs
- Large backlogs — handle thousands of items efficiently
- Sparse checkout — only cache items you care about
- Cache eviction — clean up old/irrelevant cached items
