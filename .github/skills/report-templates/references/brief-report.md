# Brief Test Report

<!--
## EXAMPLE REPORT

## Column Definitions

| Column | Description |
|--------|-------------|
| **Rule** | The specific WCAG rule ID that was violated (e.g., `color-contrast`, `focus-visible`, `keyboard-trap`) |
| **Description** | Human-readable explanation of the accessibility issue |
| **Element** | CSS selector identifying the affected element within the source |
| **WCAG Criterion** | The WCAG success criterion violated (e.g., `1.4.3 Contrast`, `2.4.7 Focus Visible`) |

## Violations

| Rule | Description | Element | WCAG Criterion |
|------|-------------|---------|----------------|
| color-contrast | Elements must have sufficient color contrast | `button.submit-btn` | 1.4.3 Contrast (Minimum) |
| focus-visible | Interactive elements must have visible focus indicator | `a.nav-link` | 2.4.7 Focus Visible |
| label | Form elements must have labels | `input#email` | 1.3.1 Info and Relationships |

---

## Notes

- **color-contrast**: The submit button uses #777777 text on #CCCCCC background (2.8:1 ratio). Recommend changing text to #595959 for 4.5:1 ratio.
- **focus-visible**: Navigation links have `outline: none` with no alternative focus indicator. Add visible focus styles.
- **label**: Email input field has placeholder text but no associated `<label>` element or `aria-label`.
-->

## Column Definitions

| Column | Description |
|--------|-------------|
| **Rule** | The specific WCAG rule ID that was violated (e.g., `color-contrast`, `focus-visible`, `keyboard-trap`) |
| **Description** | Human-readable explanation of the accessibility issue |
| **Element** | CSS selector identifying the affected element within the source |
| **WCAG Criterion** | The WCAG success criterion violated (e.g., `1.4.3 Contrast`, `2.4.7 Focus Visible`) |

## Violations
Use this table format:

| Rule | Description | Element | WCAG Criterion |
|------|-------------|---------|----------------|
| {rule-id} | {help text} | `{selector}` | {wcag criterion} |
