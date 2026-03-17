# Instructions Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | ❌ FAIL |
| 2.5.3 Label in Name | ❌ FAIL |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| label-in-name | Accessible name "Submit form" does not include visible text "Send" | `button#submit-btn` | 2.5.3 | Change aria-label to "Send" or "Send - Submit form" |
| missing-accessible-name | Button has no accessible name | `button.icon-menu` | 1.3.1 | Add `aria-label="Menu"` |
| label-in-name | Visible text "Search" not in accessible name "Find products" | `button.search-btn` | 2.5.3 | Change aria-label to include "Search" |

---

## Notes

- **button#submit-btn (label-in-name)**: Button displays "Send" but has `aria-label="Submit form"`. Voice control users saying "click Send" cannot activate it. Change to `aria-label="Send"` or `aria-label="Send - Submit form"`.
- **button.icon-menu (missing-accessible-name)**: Hamburger menu icon button has no text, aria-label, or title. Screen readers announce "button" with no indication of purpose. Add `aria-label="Open menu"` or `aria-label="Navigation menu"`.
- **button.search-btn (label-in-name)**: Search button shows magnifying glass icon + "Search" text but has `aria-label="Find products"`. The visible text "Search" must be included in the accessible name. Change to `aria-label="Search - Find products"` or remove aria-label entirely.
- Voice control users must be able to activate controls by speaking the visible label.
-->

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | {✅ PASS / ❌ FAIL} |
| 2.5.3 Label in Name | {✅ PASS / ❌ FAIL} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Notes

{Any additional observations}
