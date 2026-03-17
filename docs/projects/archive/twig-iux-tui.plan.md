# Twig IUX — Full TUI Mode Sub-Plan

> **Parent Plan**: `docs/projects/twig-interactive-ux.plan.md` — EPIC-008  
> **Date**: 2026-03-16  
> **Status**: COMPLETED  

---

## Overview

This document captures the architecture and UX specifications for the full TUI mode (Tier 3) introduced in EPIC-008. The `twig-tui` binary is a standalone Terminal.Gui v2-based application that provides a Tree Navigator and Work Item Form Editor, launched via `twig tui` from the main CLI.

---

## Architecture

```
twig (AOT CLI binary)
  └── twig tui  →  Process.Start("twig-tui") + WaitForExit() + exit code propagation
                        │
                        ▼
                   twig-tui (non-AOT binary, Terminal.Gui v2)
                        ├── Main Window
                        │   ├── MenuBar (File, Help)
                        │   ├── TreeNavigatorView (40% width, left panel)
                        │   │   └── TreeView<WorkItemNode> with ITreeBuilder
                        │   └── WorkItemFormView (60% width, right panel)
                        │       └── Editable fields + dirty indicator + save button
                        └── SQLite (shared .twig/ workspace)
```

### Binary Separation

- **`twig`** (AOT-compiled): Remains unchanged. A new `twig tui` subcommand calls `TuiLauncher.Launch()` which finds and starts the `twig-tui` process.
- **`twig-tui`** (non-AOT): Terminal.Gui v2 beta does not support Native AOT. This binary targets `net10.0` and references Terminal.Gui v2 (`2.0.0-develop.5185`). The `2.0.0-beta.*` series uses the older API surface and is incompatible with the current instance-based API (`Application.Create()`, `Terminal.Gui.App`, `Terminal.Gui.Input`, etc.); the develop build is the only v2 prerelease with the required namespace structure.

### Binary Discovery

`TuiLauncher` searches for `twig-tui` in:
1. `AppContext.BaseDirectory` (same directory as the running `twig` binary)
2. `Environment.ProcessPath` directory (AOT/single-file fallback)
3. `PATH` environment variable

---

## View Hierarchy

```
Window ("Twig TUI — {org}/{project}")
  ├── MenuBar
  │   ├── File → Refresh Tree, Exit
  │   └── Help → About, Keybindings
  ├── TreeNavigatorView (BorderStyle: Rounded)
  │   └── TreeView<WorkItemNode>
  │       └── WorkItemTreeBuilder (ITreeBuilder<WorkItemNode>)
  └── WorkItemFormView (BorderStyle: Rounded)
      ├── ID (read-only Label)
      ├── Type (read-only Label)
      ├── Title (editable TextField)
      ├── State (editable TextField)
      ├── Assigned To (editable TextField)
      ├── Iteration (read-only TextField)
      ├── Area (read-only TextField)
      ├── Dirty Indicator ("● Modified")
      ├── Save Button
      └── Status Label
```

---

## Keybinding Map

| Key | Action | Context |
|-----|--------|---------|
| `j` / `↓` | Move selection down | TreeNavigatorView |
| `k` / `↑` | Move selection up | TreeNavigatorView |
| `Enter` | Expand/collapse node | TreeNavigatorView |
| `q` | Quit application | TreeNavigatorView |
| `Esc` | Quit / close menu | Global |
| `F9` | Toggle menu bar | Global (Terminal.Gui default) |
| `Tab` | Navigate between fields | WorkItemFormView |

---

## Data Binding

### Tree Navigator

- **Data source**: `IWorkItemRepository` (SQLite cache)
- **Root resolution**: Walks parent chain from active work item to find topmost ancestor
- **Lazy loading**: `WorkItemTreeBuilder` implements `ITreeBuilder<WorkItemNode>` — children are loaded on-demand when a node is expanded
- **Node display**: `#{Id} [{Type}] {Title} ({State})` with `►` marker for active item

### Work Item Form

- **Data source**: `WorkItem` aggregate from domain layer
- **Dirty tracking**: Compares current field values against originals on every `ValueChanged` event
- **Save action**: Records changes atomically via `IPendingChangeStore.AddChangesBatchAsync()` — all field changes are written in a single SQLite transaction to prevent duplicate rows on retry after partial failure. Does NOT call `WorkItem.UpdateField()` / `.ChangeState()` — those enqueue domain commands not consumed by the TUI save path. Maintains a local `_savedEdits` dictionary so re-selecting an item after save shows the latest values.
- **Persistence**: Changes are saved locally; user runs `twig save` to push to ADO

---

## Data Flow

```
[User selects item in tree]
    → TreeNavigatorView.SelectionChanged
    → WorkItemFormView.LoadWorkItem(item)
    → Fields populated (with _savedEdits overlay if re-selecting), dirty state reset

[User edits a field]
    → TextField.ValueChanged
    → WorkItemFormView.CheckDirty()
    → Dirty indicator + Save button enabled

[User clicks Save]
    → IPendingChangeStore.AddChangesBatchAsync() — atomic transaction for all changed fields
    → _savedEdits[itemId] updated for re-select correctness
    → Status: "✓ Changes saved locally"
```

---

## Dependencies

| Dependency | Version | Purpose |
|-----------|---------|---------|
| Terminal.Gui | 2.0.0-develop.5185 | TUI framework (v2 instance-based API, non-AOT). Pinned to develop.5185 — the `2.0.0-beta.*` series uses the legacy API surface. |
| contoso.Data.Sqlite | 9.0.14 | SQLite access |
| Twig.Domain | — | Work item aggregates, interfaces |
| Twig.Infrastructure | — | SQLite persistence, config |

---

## Testing Strategy

- **Unit tests**: `WorkItemNode.ToString()`, `WorkItemTreeBuilder.CanExpand/GetChildren`, dirty tracking logic, `OnSave` behavior (batch-changed-fields-only, originals update, no-op when clean, exception handling, no duplicate persist, multi-field partial failure retry safety), `ExpandToActiveAsync` multi-sibling traversal
- **Integration tests**: Not feasible for full Terminal.Gui rendering in CI (requires terminal); TUI logic is tested via unit tests on individual components
- **Manual testing**: Launch `twig-tui`, navigate tree, edit fields, verify save

---

## Design Decisions

### Exception Handling: `catch (Exception)` vs bare `catch {}`

The codebase uses `catch (Exception)` rather than bare `catch {}` in two places:

1. **`WorkItemTreeBuilder.CanExpand`** — catches config lookup failures to fall back to hardcoded leaf detection.
2. **`WorkItemTreeBuilder` constructor pre-warm task** — catches all exceptions from the fire-and-forget `GetConfiguration()` call.

**Rationale**: `catch (Exception)` is preferred over bare `catch {}` because bare `catch {}` also catches non-CLS-compliant exceptions (objects not deriving from `System.Exception`, typically thrown from native interop code), while `catch (Exception)` does not. Both forms catch `OutOfMemoryException`, `StackOverflowException`, and other `SystemException` subclasses — `catch (Exception)` does NOT prevent those from being caught. The choice of `catch (Exception)` is a code clarity and convention improvement, not a safety one.

The pre-warm task uses `catch (Exception ex) when (ex is not OutOfMemoryException)` to suppress transient errors that would otherwise surface as unobserved task exceptions on the GC finalizer thread, while letting truly fatal OOM exceptions propagate for a clean crash. `CanExpand` has its own `catch (Exception)` fallback on the UI thread where a meaningful fallback (hardcoded leaf detection) exists.

### Atomic Batch Save

`WorkItemFormView.OnSave` uses `IPendingChangeStore.AddChangesBatchAsync()` to persist all changed fields in a single SQLite transaction. This prevents the duplicate-row-on-retry problem: if the save fails, zero rows are inserted, so the next save attempt produces exactly the same batch without duplicating previously-succeeded inserts.

---

## Future Enhancements

- Async data loading with progress indicators
- Search/filter in tree navigator
- Inline state transition picker (dropdown instead of text field)
- ADO push from within TUI (currently deferred to `twig save`)
- Configurable keybindings via `.twig/config`
