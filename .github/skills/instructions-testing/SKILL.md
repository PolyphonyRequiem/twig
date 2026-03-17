---
name: instructions-testing
description: Use when testing that native widget labels and instructions are programmatically determinable (WCAG 1.3.1, 2.5.3).
---

# Instructions Testing

Test that native widgets with visible labels or instructions have them programmatically determinable.

**WCAG Success Criterion:**
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)
- [Understanding 2.5.3 Label in Name](https://www.w3.org/WAI/WCAG21/Understanding/label-in-name.html)

## Why It Matters

People with good vision can identify a widget's label and instructions by visually scanning the page and interpreting visual characteristics such as proximity. To provide an equivalent experience for people who use assistive technologies, a widget's label and instructions must be programmatically related to it.

### From a User's Perspective

- **Voice control users** rely on programmatically related labels to activate controls by speaking the visible label text
- **Screen reader users** depend on programmatic labels to understand what each form control does

> *Note: Both WCAG 2.0 and 2.1 require a widget's visible label and instructions (if present) to be programmatically determinable. WCAG 2.1 also requires a widget's visible label and instructions (if present) to be included in its accessible name and description.*

## Native Widgets in Scope

This test applies to native HTML widgets including:
- `<button>` elements
- `<input>` elements (text, checkbox, radio, etc.)
- `<select>` elements
- `<textarea>` elements

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Detect Iframes

Use `mcp_playwright_browser_run_code` to find all iframes within the user-defined `Target` element or the entire page if no target is specified:

```javascript
async (page, targetSelector) => {
  const iframes = await page.evaluate((selector) => {
    const root = selector ? document.querySelector(selector) : document;
    if (!root) return [];
    const frames = root.querySelectorAll('iframe');
    return Array.from(frames).map((frame, index) => ({
      index,
      selector: frame.id ? `iframe#${frame.id}` : 
                frame.name ? `iframe[name="${frame.name}"]` : 
                `iframe:nth-of-type(${index + 1})`,
      src: frame.src || '(no src)',
      title: frame.title || '(no title)'
    }));
  }, targetSelector);
  return JSON.stringify(iframes, null, 2);
}
```

**After detecting iframes:**
1. Test the main `Target` first (Steps 2-4).
2. Then test each iframe individually by accessing it via `page.frames()`
3. Aggregate results from all contexts

**Why this matters:** Due to CORS restrictions, you cannot evaluate JavaScript inside cross-origin iframes from the main page context. Each iframe must be tested separately using Playwright's `page.frames()` method.

---

### Step 2: Identify Native Widgets

In the target page (and each iframe), examine each native widget (`<button>`, `<input>`, `<select>`, `<textarea>`) to determine whether it has a visible label or instructions.

### Step 3: Verify Programmatic Association

If a widget does have a visible label or instructions, verify that they are programmatically associated:
- The **accessible name** must be (or include) an exact match of any visible text label.
- The **accessible description** must include any additional visible instructions. If any non-text instructions are provided (for example, icons or color changes), the accessible description must include a text equivalent.

### Step 4: Record Results

Record your results:
- Select **Fail** for any instances that do not meet the requirement.
- Otherwise, select **Pass**. Or, after you have marked all failures, select **Pass unmarked instances**.

## Pass/Fail Criteria

### ✅ Pass

- **Accessible name matches visible label:** The widget's accessible name contains the exact visible text label
- **Instructions are programmatically associated:** Any visible instructions are included in the accessible description
- **Non-text instructions have text equivalents:** Icons or color-based instructions have corresponding text alternatives

### ❌ Fail

Record as **FAIL** if:

- **Accessible name doesn't match visible label:** The visible text label is not included in the widget's accessible name
- **Missing label association:** A visible label exists near the widget but is not programmatically associated (no `<label>`, `aria-label`, or `aria-labelledby`)
- **Instructions not programmatically related:** Visible instructions exist but are not included in the accessible description
- **Non-text instructions lack text equivalent:** Icon or color-based instructions have no text alternative

## References

### WCAG Success Criteria
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)
- [Understanding 2.5.3 Label in Name](https://www.w3.org/WAI/WCAG21/Understanding/label-in-name.html)

### Sufficient Techniques
- [Ensure the "accessible name" includes the visible text](https://www.w3.org/WAI/WCAG21/Techniques/general/G208)
- [Using label elements to associate text labels with form controls](https://www.w3.org/WAI/WCAG21/Techniques/html/H44)
- [Using HTML form controls and links](https://www.w3.org/WAI/WCAG21/Techniques/html/H91)
- [Using aria-label to provide an invisible label where a visible label cannot be used](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA14)
- [Using aria-labelledby to provide a name for user interface controls](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA16)
- [Using the title attribute to identify form controls when the label element cannot be used](https://www.w3.org/WAI/WCAG21/Techniques/html/H65)
- [Providing a description for groups of form controls using fieldset and legend elements](https://www.w3.org/WAI/WCAG21/Techniques/html/H71)

### Common Failures

- [Failure due to "accessible name" not containing the visible label text](https://www.w3.org/WAI/WCAG21/Techniques/failures/F96)

### Additional Guidance

- [Positioning labels to maximize predictability of relationships](https://www.w3.org/WAI/WCAG21/Techniques/general/G162)
- [Using the aria-describedby property to provide a descriptive label for user interface controls](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA1)
