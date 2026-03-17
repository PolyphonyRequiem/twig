# Axe-Core Report

<!--
## EXAMPLE REPORT

## Summary

| URL | https://example.com/form |
|-----|-------------------------|
| **Target** | `body` |
| **Date** | February 18, 2026 |
| **WCAG Tags** | wcag2a, wcag2aa, wcag21aa |
| **Result** | ❌ FAIL |
| **Violations** | 3 |

---

## Results

| Level | Violations |
|-------|------------|
| Level A | 1 |
| Level AA | 2 |
| Level AAA | 0 |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| color-contrast | Elements must have sufficient color contrast | `p.disclaimer` | 1.4.3 | Increase text color contrast to at least 4.5:1 |
| label | Form elements must have labels | `input#phone` | 1.3.1 | Add `<label for="phone">` or `aria-label` |
| image-alt | Images must have alternate text | `img.hero-banner` | 1.1.1 | Add descriptive `alt` attribute |

---

## Notes

- **color-contrast (p.disclaimer)**: Gray text (#767676) on white background has 4.48:1 ratio, just below 4.5:1 minimum. Darken to #757575.
- **label (input#phone)**: Phone number field relies on placeholder text "Enter phone number" but has no programmatic label. Screen readers won't announce the field purpose.
- **image-alt (img.hero-banner)**: Hero banner image shows product lineup but has no alt text. Recommend: `alt="2026 Spring Collection featuring three new laptop models"`.
- Ran with axe-core 4.8.0, testing WCAG 2.1 Level AA.
-->

## Summary

| URL | {url} |
|-----|-------|
| **Target** | {selector} |
| **Date** | {date} |
| **WCAG Tags** | {wcag2a, wcag2aa, wcag21a, etc.} |
| **Result** | {✅ PASS / ❌ FAIL} |
| **Violations** | {count} |

---

## Results

| Level | Violations |
|-------|------------|
| Level A | {count} |
| Level AA | {count} |
| Level AAA | {count} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Notes

{Any additional observations}
