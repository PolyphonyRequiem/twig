# Bypass Block Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 2.4.1 Bypass Blocks (Level A) | ❌ FAIL |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| bypass | Page does not have a mechanism to bypass repeated blocks | `body` | 2.4.1 | Add "Skip to main content" link at the top of the page |
| region | All page content should be contained by landmarks | `div.promo-banner` | 2.4.1 | Wrap in appropriate landmark (`<aside>`, `<nav>`, or `role`) |
| landmark-one-main | Page should contain one main landmark | `body` | 2.4.1 | Add `<main>` element or `role="main"` to primary content area |

---

## Notes

- **bypass (body)**: Page has 45 focusable elements in the header/navigation before main content. Keyboard users must Tab through all of them to reach content. Add `<a href="#main-content" class="skip-link">Skip to main content</a>` as first focusable element.
- **region (div.promo-banner)**: Promotional banner floats outside any landmark region. Screen reader users using landmark navigation will miss this content. Wrap in `<aside aria-label="Promotional offers">` or similar.
- **landmark-one-main (body)**: Page content is in `<div class="content">` with no landmark role. Screen reader users cannot jump directly to main content. Change to `<main>` element or add `role="main"`.
- Skip links should be the first focusable element on the page and visible on focus.
-->

## Results

| Test | Result |
|------|--------|
| 2.4.1 Bypass Blocks (Level A) | {✅ PASS / ❌ FAIL} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Notes

{Any additional observations}
