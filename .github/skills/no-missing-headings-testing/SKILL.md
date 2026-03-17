---
name: no-missing-headings-testing
description: Use when testing no missing headings (WCAG 1.3.1, 2.4.6).
---

# No Missing Headings Testing

## Description

Guide for testing that text which visually appears as a heading is properly coded as a heading (WCAG 1.3.1 Info and Relationships, WCAG 2.4.6 Headings and Labels).

**WCAG Criteria**:
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)
- [Understanding Success Criterion 2.4.6: Headings and Labels](https://www.w3.org/WAI/WCAG21/Understanding/headings-and-labels.html)

## Why It Matters

People with good vision can quickly scan a page to identify headings based solely on their appearance, such as large or bold font, preceding white space, or indentation. Users of assistive technologies can't find headings that aren't properly coded.

### From a User's Perspective

> "When I use a screen reader, I navigate by headings to quickly find the content I need. If a heading looks like a heading visually but isn't coded as one, I can't find it—it's invisible to my navigation. I have to listen through everything to find what sighted users can spot in seconds."

## Key Concepts

### What Requires Heading Markup

Text that **looks like** a heading must be **coded** as a heading. Visual indicators that suggest heading treatment include:

- Large font size
- Bold or heavier font weight
- Preceding white space or separation
- Indentation or distinct positioning
- Different color or styling that sets it apart from body text

### Proper Heading Implementation

- Use native HTML heading elements (`<h1>` through `<h6>`)
- While you could add `role="heading"` to a different element, the first rule of ARIA is to use native HTML elements where possible, instead of repurposing an element by adding ARIA
- Heading levels should reflect the logical structure of the content

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Identify Visual Headings

Examine the target page to identify all text that **visually appears** to be a heading based on:
- Font size larger than surrounding text
- Bold or emphasized styling
- Separation from other content (white space above/below)
- Text that introduces or labels a section of content

### Step 2: Verify Heading Markup

For each visual heading identified:

1. **Inspect the element** in the accessibility tree or DOM
2. **Check if it uses** proper heading markup:
   - `<h1>`, `<h2>`, `<h3>`, `<h4>`, `<h5>`, or `<h6>` elements
   - Or `role="heading"` with appropriate `aria-level`
3. **Verify the heading level** is appropriate for the content hierarchy

### Step 3: Record Results

**✅ Pass** if:
- All text that looks like a heading is coded as a heading
- Heading elements are used appropriately to represent content structure

**❌ Fail** if:
- Text that visually appears as a heading is not coded as a heading (e.g., styled `<div>`, `<span>`, or `<p>` without heading semantics)
- Visual styling creates the appearance of headings without corresponding markup

## References

### WCAG Success Criteria
- [Understanding Success Criterion 1.3.1: Info and Relationships](https://www.w3.org/WAI/WCAG21/Understanding/info-and-relationships.html)
- [Understanding Success Criterion 2.4.6: Headings and Labels](https://www.w3.org/WAI/WCAG21/Understanding/headings-and-labels.html)

### Sufficient Techniques
- [G130: Providing descriptive headings](https://www.w3.org/WAI/WCAG21/Techniques/general/G130)
- [H42: Using h1 - h6 to identify headings](https://www.w3.org/WAI/WCAG21/Techniques/html/H42)
- [G141: Organizing a page using headings](https://www.w3.org/WAI/WCAG21/Techniques/general/G141)

### Common Failures
- [F43: Failure of Success Criterion 1.3.1 due to using structural markup in a way that does not represent relationships in the content](https://www.w3.org/WAI/WCAG21/Techniques/failures/F43)