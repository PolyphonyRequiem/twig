# Image Function Test Report

<!--
## EXAMPLE REPORT

## Summary

| Metric | Value |
|--------|-------|
| **Total Images Tested** | 12 |
| **Meaningful (Pass)** | 6 |
| **Decorative (Pass)** | 4 |
| **Violations** | 1 |
| **Needs Manual Review** | 1 |
| **Compliance Status** | ❌ Fail |

---

## Violations

| Image | Issue | Element |
|-------|-------|--------|
| `product-photo.jpg` | Missing alt text on meaningful image | `img.product-image` |

---

## Meaningful Images

| Image Source | Accessible Name | Element |
|--------------|-----------------|--------|
| `logo.png` | "Acme Corp Logo" | `img.header-logo` |
| `chart.svg` | "Sales growth Q1-Q4 2025" | `svg.chart` |

---

## Decorative Images

| Image Source | Technique | Element |
|--------------|-----------|--------|
| `divider.png` | `alt=""` | `img.section-divider` |
| `bg-pattern.svg` | `aria-hidden="true"` | `svg.background` |

---

## Images Requiring Manual Review

| Image | Current State | Context | Recommendation |
|-------|---------------|---------|----------------|
| `team-photo.jpg` | has alt="image" | About Us section | Replace with descriptive alt like "Engineering team at annual retreat" |

---

## Notes

- **product-photo.jpg (img.product-image)**: Product image on e-commerce page has no alt text. This is a meaningful image showing the product. Screen reader users cannot identify what product is displayed. Recommend: `alt="Blue wireless headphones model XR-500"`.
- **team-photo.jpg (manual review)**: Has generic alt="image" which is worse than no alt. The image shows 12 team members at a company event. Should describe the content: `alt="Engineering team of 12 people at the 2025 annual company retreat"`.
- Decorative images (divider.png, bg-pattern.svg) correctly use `alt=""` or `aria-hidden="true"`.
- 12 images tested; 1 requires remediation, 1 needs manual review.
-->

## Summary

| Metric | Value |
|--------|-------|
| **Total Images Tested** | {count} |
| **Meaningful (Pass)** | {count} |
| **Decorative (Pass)** | {count} |
| **Violations** | {count} |
| **Needs Manual Review** | {count} |
| **Compliance Status** | ✅ Pass / ❌ Fail |

---

## Violations

> If no violations, display: "✅ No violations found"

| Image | Issue | Element |
|-------|-------|---------|
| `{src or description}` | {issue description} | `{selector}` |

---

## Meaningful Images

Images that convey information with proper accessible names

| Image Source | Accessible Name | Element |
|--------------|-----------------|---------|
| `{filename}` | "{alt text}" | `{selector}` |

---

## Decorative Images

Images correctly coded to be ignored by assistive technology

| Image Source | Technique | Element |
|--------------|-----------|---------|
| `{filename}` | `alt=""` / `aria-hidden="true"` | `{selector}` |

---

## Images Requiring Manual Review

Images that need human judgment to determine if meaningful or decorative

| Image | Current State | Context | Recommendation |
|-------|---------------|---------|----------------|
| `{src}` | {has alt / missing alt} | {surrounding content} | {suggested action} |

### Review Questions

For each image requiring review, ask:
1. Does this image convey information not available elsewhere on the page?
2. Would removing this image impact understanding of the content?
3. Is this image purely decorative (visual appeal or layout)?

---

## Recommendations

| Current State | Suggested Fix |
|---------------|---------------|
| `<img src="logo.png">` (no alt) | Add `alt="Company Name Logo"` or `alt=""` if decorative |
| `<img alt="image1.jpg">` | Replace with descriptive text like `alt="Product photo"` |
| `<svg>...</svg>` (no ARIA) | Add `role="img" aria-label="description"` or `aria-hidden="true"` |

---

## Notes

{Any additional observations about image accessibility on this page}
