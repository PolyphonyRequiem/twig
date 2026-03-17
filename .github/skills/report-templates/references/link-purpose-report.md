# Link Purpose Test Report

<!--
## EXAMPLE REPORT

## Summary

| Metric | Value |
|--------|-------|
| **Total Links Tested** | 15 |
| **Clear Purpose** | 12 |
| **Context Provides Purpose** | 1 |
| **Violations** | 2 |
| **Compliance Status** | ❌ Fail |

---

## Violations

| Link Text | Issue | Location |
|-----------|-------|----------|
| "Click here" | Purpose unclear without surrounding context | `#pricing-section a:nth-child(2)` |
| "" (empty) | Empty accessible name | `a.social-icon.twitter` |

---

## Links Requiring Context

| Link Text | Context | Assessment |
|-----------|---------|------------|
| "Learn more" | Follows paragraph about pricing plans | ✅ Context provides purpose |

---

## Recommendations

| Current Link Text | Suggested Improvement |
|-------------------|----------------------|
| "Click here" | "View pricing details" |
| "Read more" | "Read more about our services" |

---

## Notes

- **"Click here" (#pricing-section a:nth-child(2))**: Link appears in sentence "For more details, click here." Voice users cannot activate by saying "click here" as it's ambiguous. Screen reader users hear "click here, link" with no context. Recommend: "View pricing details".
- **Empty link (a.social-icon.twitter)**: Twitter icon link has no text, aria-label, or title. Screen readers announce "link" with no description. Add `aria-label="Follow us on Twitter"`.
- **"Learn more"**: Passes because it follows a paragraph about pricing plans, providing sufficient context per WCAG 2.4.4.
- Total of 15 links tested on the page.
-->

## Summary

| Metric | Value |
|--------|-------|
| **Total Links Tested** | {count} |
| **Clear Purpose** | {count} |
| **Context Provides Purpose** | {count} |
| **Violations** | {count} |
| **Compliance Status** | ✅ Pass / ❌ Fail |

---

## Violations

> If no violations, display: "✅ No violations found"

| Link Text | Issue | Location |
|-----------|-------|----------|
| "{link text}" | {issue description} | `{selector or context}` |

### Issue Types

| Issue | Description |
|-------|-------------|
| **Empty accessible name** | Link has no text, aria-label, or title |
| **Duplicate text, different destinations** | Same link text leads to different URLs |
| **Purpose unclear** | Cannot determine purpose from text or context |

---

## Links Requiring Context

> Links that pass but rely on surrounding context for clarity

| Link Text | Context | Assessment |
|-----------|---------|------------|
| "{link text}" | {surrounding context} | ✅ Context provides purpose |

---

## Recommendations (Optional)

| Current Link Text | Suggested Improvement |
|-------------------|----------------------|
| "Learn more" | "Learn more about {topic}" |
| "Click here" | "{Descriptive action}" |

---

## Notes

{Any additional observations}
