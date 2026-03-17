---
name: heading-levels-testing
description: Use when testing heading level accessibility (WCAG 1.3.1 Info and Relationships).
---

# Heading Level Testing

Test that heading levels are properly structured so that programmatic heading levels match their visual presentation.

**WCAG Success Criterion:** 1.3.1 Info and Relationships (Level A)
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)


## Why It Matters

Heading levels communicate the relative importance of headings within a page. People with good vision can infer heading levels through visual cues—higher-level headings typically have greater visual prominence than lower-level headings. Users of assistive technology rely on programmatic cues to perceive heading levels.

**Impact:** Without proper heading levels, screen reader users cannot:
- Understand the document structure and hierarchy
- Navigate efficiently between sections
- Determine which content is more or less important

## Critical Testing Concepts

### Programmatic vs Visual Level

- **Programmatic level:** The HTML heading tag used (`<h1>`, `<h2>`, `<h3>`, etc.)
- **Visual level:** The heading's appearance based on font size, weight, and styling

**Requirement:** These two must match. A heading that looks like a level 1 heading must be coded as `<h1>`.

### Heading Hierarchy Rules

1. **Level 1 headings should be most prominent** (largest/boldest)
2. **Level 6 headings should be least prominent** (smallest)
3. **Headings of the same level should have consistent styling**
4. **Hierarchy should be logical** (don't skip levels unnecessarily)

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Take a Page Snapshot

1. Use `mcp_playwright_browser_snapshot` to capture the current page state
2. Review the snapshot to identify all elements with heading roles (`h1`-`h6`)
3. Create a list of all headings with their:
   - Programmatic level (from the snapshot)
   - Visual text content
   - Position on the page

### Step 2: Visual Assessment

**For each heading identified, examine its visual styling:**

1. Note the font size relative to other headings
2. Note the font weight (bold, regular, light)
3. Note any other visual styling (color, spacing, capitalization)
4. Determine what heading level the visual styling suggests

**Example visual hierarchy assessment:**
- Very large, bold text → Suggests h1
- Large, bold text → Suggests h2
- Medium, bold text → Suggests h3
- Normal size, bold text → Suggests h4
- Normal size, regular text → Suggests h5 or h6

### Step 3: Compare Programmatic vs Visual

For each heading, compare the programmatic level to the visual level:

1. **Match:** Programmatic and visual levels align → **PASS**
2. **Mismatch:** Programmatic level doesn't match visual prominence → **FAIL**

**Common failure examples:**
- Coded as `<h2>` but styled to look more prominent than `<h1>`
- Coded as `<h4>` but styled identically to `<h2>`
- Coded as `<h1>` but styled smaller than surrounding `<h2>` elements

### Step 4: Check Heading Consistency

**Verify that headings of the same level have consistent styling:**

1. Group headings by their programmatic level
2. For each group, check that all headings have similar:
   - Font size
   - Font weight
   - Visual prominence

### Step 5: Assess Logical Hierarchy

Check the overall heading structure:

1. Page should typically start with an `<h1>` (main page title)
2. Subsections use `<h2>`
3. Sub-subsections use `<h3>`, and so on
4. Avoid skipping levels going down (e.g., `<h2>` → `<h4>`)
   - Exception: Skipping up is acceptable (`<h4>` → `<h2>` when returning to a higher section)

## Pass/Fail Criteria

### ✅ Pass

- **All headings have programmatic levels matching their visual presentation**
- **Headings of the same level have consistent font styling**
- **Lower-level headings are more prominent than higher-level headings**
  - h1 is most prominent
  - h6 is least prominent
- **Logical hierarchy is maintained** (no unnecessary level skips)

### ❌ Fail

**Record as FAIL if ANY of the following occur:**

- **Visual-programmatic mismatch:** A heading's coded level doesn't match its visual prominence
- **Inconsistent styling:** Headings of the same level look different from each other
- **Inverted hierarchy:** A lower-level heading appears more visually prominent than a higher-level heading
- **Style masquerading as heading:** Styled text that looks like a heading but isn't coded as one (`<div>` styled to look like `<h2>`)

## References

### WCAG Success Criteria
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)

### Sufficient Techniques
- [Using h1-h6 to identify headings](https://www.w3.org/WAI/WCAG21/Techniques/html/H42)