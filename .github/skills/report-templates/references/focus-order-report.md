# Focus Order Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 2.4.3 Focus Order (Level A) | ❌ FAIL |

---

## Violations

| Element | Issue | Fix |
|---------|-------|-----|
| Modal dialog | Focus moves behind modal when opened | Trap focus within modal using JavaScript |
| Sidebar navigation | Focus jumps to footer before main content | Adjust DOM order or use `tabindex` to fix sequence |
| Form submit button | Focus skips to unrelated section after submission | Return focus to form or confirmation message |

---

## Composite Widgets Tested

| Widget | Role | Items | Navigation | Result |
|--------|------|-------|------------|--------|
| Tab panel | `tablist` | 4 | Arrow keys | ✅ |
| Dropdown menu | `menu` | 6 | Arrow keys | ❌ |
| Data grid | `grid` | 5×8 | Arrow keys | ✅ |

---

## Notes

- **Modal dialog**: When modal opens, focus remains on the trigger button behind the modal overlay. Users can Tab into page content behind the modal. Implement focus trap: move focus to modal on open, cycle focus within modal, return focus to trigger on close.
- **Sidebar navigation**: DOM order places sidebar after footer. CSS positions it visually on the left. Focus sequence is: header → main content → footer → sidebar. Reorder DOM or use `tabindex` to match visual order.
- **Form submit button**: After form submission, focus jumps to an unrelated "Related Products" section instead of the success message. Use `element.focus()` to move focus to confirmation message.
- **Dropdown menu**: Arrow keys don't work within menu items. Implements click but not keyboard navigation pattern.
- Focus order should follow logical reading order (left-to-right, top-to-bottom for LTR languages).
-->

## Results

| Test | Result |
|------|--------|
| 2.4.3 Focus Order (Level A) | {✅ PASS / ❌ FAIL} |

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
