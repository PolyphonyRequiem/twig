# Heading Level Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | ❌ FAIL |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| heading-order | Heading levels should only increase by one | `h4.card-title` | 1.3.1 | Change from `<h4>` to `<h3>` (skipped h3 level) |
| empty-heading | Headings must not be empty | `h2#section-title` | 1.3.1 | Add text content or remove empty heading |
| heading-order | Page should contain a level-one heading | `body` | 1.3.1 | Add `<h1>` as the main page title |

---

## Notes

- **h4.card-title (heading-order)**: Card titles use `<h4>` but parent section uses `<h2>`. Skips `<h3>` level. Screen reader users navigating by headings will find the hierarchy confusing. Change card titles to `<h3>`.
- **h2#section-title (empty-heading)**: Heading element exists in DOM but contains no text (may be populated by JavaScript that failed). Either add content or remove the empty element.
- **body (missing h1)**: Page has no `<h1>` element. The page title "Dashboard" is styled as large text but uses `<div class="page-title">`. Change to `<h1>Dashboard</h1>`.
- Heading hierarchy: h1 → h2 → h3 → h4 (no skipping levels).
-->

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | {✅ PASS / ❌ FAIL} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Notes

{Any additional observations}
