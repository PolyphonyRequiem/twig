# No Missing Headers Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | ❌ FAIL |
| 2.4.6: Headings and Labels | ❌ FAIL |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| p-as-heading | Text styled as heading but not coded as heading | `p.section-title` | 1.3.1 | Change `<p class="section-title">` to `<h2>` |
| p-as-heading | Bold large text appears to be a heading | `p strong` ("Our Services") | 1.3.1 | Use semantic heading element `<h3>Our Services</h3>` |
| missing-heading | Section lacks identifying heading | `section.features` | 2.4.6 | Add heading to identify section purpose |

---

## Notes

- **p.section-title (p-as-heading)**: Text "Our Products" is styled with `font-size: 24px; font-weight: bold` making it look like a heading, but it's a `<p>` element. Screen reader users navigating by headings will skip this section. Change to `<h2>Our Products</h2>`.
- **p strong "Our Services" (p-as-heading)**: Bold text inside paragraph looks like a subheading visually. It introduces a list of services. Should be `<h3>Our Services</h3>` to be discoverable via heading navigation.
- **section.features (missing-heading)**: Features section contains 6 feature cards but has no heading to identify the section. Add `<h2>Features</h2>` at the start of the section for screen reader users navigating by landmarks.
- Text that looks like a heading should be coded as a heading for screen reader users.
-->

## Results

| Test | Result |
|------|--------|
| 1.3.1: Info and Relationships | {✅ PASS / ❌ FAIL} |
| 2.4.6: Headings and Labels | {✅ PASS / ❌ FAIL} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Notes

{Any additional observations}
