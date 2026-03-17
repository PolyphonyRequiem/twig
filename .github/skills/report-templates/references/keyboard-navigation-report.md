# Keyboard Navigation Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 2.1.1 Keyboard (Level A) | ❌ FAIL |

---

## Violations

| Element | Issue | Fix |
|---------|-------|-----|
| Custom dropdown `div.dropdown-menu` | Cannot be activated with Enter/Space keys | Add `role="listbox"` and keyboard event handlers |
| Modal close button | Not reachable via Tab key | Add `tabindex="0"` or use native `<button>` |

---

## Composite Widgets Tested

| Widget | Role | Items | Navigation | Result |
|--------|------|-------|------------|--------|
| Main navigation | `menubar` | 5 | Arrow keys | ✅ |
| Data table | `grid` | 4×10 | Arrow keys | ✅ |
| Settings tabs | `tablist` | 3 | Arrow keys | ❌ |

---

## Notes

- **Custom dropdown (div.dropdown-menu)**: Built with `<div>` elements instead of native `<select>`. Clicking works but Enter/Space keys do nothing. Needs `keydown` event handler for Enter (keyCode 13) and Space (keyCode 32).
- **Modal close button**: The X button is a `<span>` with click handler. Not in tab order. Replace with `<button>` or add `tabindex="0"` and `role="button"`.
- **Settings tabs**: Arrow key navigation not implemented. Left/Right arrows should move between tabs per ARIA tabs pattern. Currently requires Tab key to move between each tab.
- Tested using keyboard-only navigation (Tab, Shift+Tab, Enter, Space, Arrow keys, Escape).
-->

## Results

| Test | Result |
|------|--------|
| 2.1.1 Keyboard (Level A) | {✅ PASS / ❌ FAIL} |

---

## Violations

| Element | Issue  | Fix |
|---------|-------|------|
| {element name or description} | {description} | {recommendation} |

---

## Composite Widgets Tested

| Widget | Role | Items | Navigation | Result |
|--------|------|-------|------------|--------|
| {name} | `menubar` | {count} | Arrow keys | {✅/❌} |
| {name} | `grid` | {cols}×{rows} | Arrow keys | {✅/❌} |
| {name} | `tablist` | {count} | Arrow keys | {✅/❌} |

---

## Notes

{Any additional observations}
