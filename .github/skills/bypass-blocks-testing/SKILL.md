---
name: bypass-blocks-testing
description: Use when testing bypass blocks accessibility (WCAG 2.4.1 Bypass Blocks).
---

# Bypass Blocks Testing

Test that pages provide a keyboard-accessible method to bypass repetitive content.

**WCAG Success Criterion:** 2.4.1 Bypass Blocks (Level A)
- [Understanding Success Criterion 2.4.1: Bypass Blocks](https://www.w3.org/WAI/WCAG21/Understanding/bypass-blocks.html)

## Why It Matters

Web pages typically have blocks of content that repeat across multiple pages, such as banners and site navigation menus. A person who uses a mouse can visually skim past that repeated content to access a link or other control within the primary content with a single click.

Similarly, a bypass mechanism allows keyboard users to navigate directly to the page's main content. Otherwise, reaching the primary content could require dozens of keystrokes. People with limited mobility could find this task difficult or painful, and people who use screen readers could find it tedious to listen as each repeated element is announced.

### From a User's Perspective

> *"I navigate content and interfaces using a screen reader and a keyboard. Repeated blocks of navigation and content force me to 're-read' everything as I work back and forth over the interface to complete a task or, enjoy content. Allow me a way to 'bypass' repetitive blocks of navigation and content via keyboard commands, skip links, and WAI-ARIA regions."*

## Bypass Mechanism Types

A page can satisfy this requirement using one or more of these techniques:

### Skip Links
- A skip link at the top of the page
- Links at the beginning of repeated content blocks that skip to the end
- Links at the top of the page that navigate to each section

### ARIA Landmarks
- Using landmark roles (`main`, `navigation`, `banner`, `contentinfo`, etc.)
- Allows screen reader users to jump between regions

### Structural Elements
- Providing headings (`<h1>`-`<h6>`) at the beginning of each section
- Using `<main>`, `<nav>`, `<header>`, `<footer>` HTML5 elements

### Frame Organization
- Using frame elements to group blocks of repeated content
- Adding `title` attribute to `<frame>` and `<iframe>` elements

### Collapsible Content
- Containing repeated content in a collapsible menu that can be skipped

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Examine the target page to identify:
   - The starting point of the page's primary content.
   - Any blocks of content that (1) precede the primary content and (2) appear on multiple pages, such as banners, navigation links, and advertising frames.

### Step 2. Use the **Tab** key to navigate toward the primary content. As you navigate, look for a bypass mechanism (typically a skip link). The mechanism might not become visible until it receives focus.

### Step 3. If a bypass mechanism *does not* exist, mark the test as **Fail**.

### Step 4. If a bypass mechanism *does* exist, activate it.

### Step 5. Verify that focus shifts past any repetitive content to the page's primary content.

### Step 6. Record your results:
   - If you find a failure, mark as **Fail**, then add the failure instance.
   - Otherwise, mark as **Pass**.

## Pass/Fail Criteria

### ✅ Pass

The page passes if these bypass mechanisms exist and function correctly:

- **Skip link present:** A keyboard-accessible link that moves focus past repetitive content to the main content area
- **Landmark regions defined:** Proper ARIA landmarks (`main`, `navigation`, etc.) allow assistive technology to jump between regions
- **Table of contents:** Links at the top of the page that navigate to each section of content
- **Collapsible navigation:** Repeated content can be collapsed/hidden to reduce Tab stops
- **Framed content:** Repeated content is contained in properly titled frames

### ❌ Fail

Record as **FAIL** if:

- **No bypass mechanism exists:** User must Tab through all repetitive content to reach the main area
- **Skip link doesn't work:** The skip link is present but doesn't move focus correctly
- **Skip link not keyboard accessible:** The skip link cannot be reached or activated via keyboard
- **Skip link hidden from keyboard users:** The skip link is only accessible to screen readers, not keyboard-only users
- **No landmarks AND no skip link:** The page has neither proper landmarks nor skip links

## References

### WCAG Success Criteria
- [Understanding Success Criterion 2.4.1: Bypass Blocks](https://www.w3.org/WAI/WCAG21/Understanding/bypass-blocks.html)

### Sufficient Techniques
Use one of these techniques to provide a link for bypassing repeated content:
- [Adding a 'skip link' at the top of the page that navigates directly to the main content](https://www.w3.org/WAI/WCAG21/Techniques/general/G1)
- [Adding a link at the beginning of a block of repeated content that navigates to the end of the block](https://www.w3.org/WAI/WCAG21/Techniques/general/G123)
- [Adding links at the top of the page that navigate to each section of content](https://www.w3.org/WAI/WCAG21/Techniques/general/G124)

- [Using ARIA landmarks to identify regions of a page](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA11)
- [Providing headings at the beginning of each section of content](https://www.w3.org/WAI/WCAG21/Techniques/html/H69)
- [Using frame elements to group blocks of repeated content](https://www.w3.org/WAI/WCAG21/Techniques/html/H70)
and
- [Using the title attribute of the frame and iframe elements](https://www.w3.org/WAI/WCAG21/Techniques/html/H64)
- [Containing repeated content in a collapsible menu](https://www.w3.org/WAI/WCAG21/Techniques/client-side-script/SCR28)
