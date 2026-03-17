---
name: reflow-testing
description: Use when testing content reflow accessibility (WCAG 1.4.10 Reflow).
---

# Reflow Testing

Test that content is visible without having to scroll in two dimensions.

**WCAG Success Criterion:**
- [Understanding Success Criterion 1.4.10: Reflow](https://www.w3.org/WAI/WCAG21/Understanding/reflow.html)

## Why It Matters

Having to scroll in two directions is difficult for everyone. Having to scroll in the direction of reading makes reading especially difficult for people with certain disabilities, including:

- **People with low vision** who are more likely to need enlarged text in a single column
- **People with reading disabilities** that make it difficult to visually track long lines of text
- **People with motor disabilities** who find scrolling difficult

## Scope

This test applies to pages that use a script read horizontally (left-to-right or right-to-left) rather than vertically (top-to-bottom).

### Exceptions

Two-dimensional scrolling is allowed for parts of the content which require two-dimensional layout for usage or meaning:

- **Images required for understanding** (such as maps and diagrams)
- **Video**
- **Games**
- **Presentations**
- **Data tables** (not individual cells)
- **Interfaces where it is necessary to keep toolbars in view** while manipulating content

> **Important:** The exception only applies to the specific section of content that requires two-dimensional layout. Other content on the same page must still reflow. For example, if a page contains a data table and paragraphs, the table is excepted but the paragraphs must still reflow within 320 CSS pixels.

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Set Display Resolution

Use your system's display settings to set the display resolution to **1280 x 1024**.

> **Note:** If testing via Playwright, you can set the viewport size programmatically.

Use `mcp_playwright_browser_resize` or `mcp_playwright_browser_run_code` to set the viewport:

```javascript
async (page) => {
  // Set viewport to 1280x1024
  await page.setViewportSize({ width: 1280, height: 1024 });
  return 'Viewport set to 1280x1024';
}
```

### Step 2: Set Zoom to 400%

Use the browser's settings to set the target page's zoom to **400%** and enable full-screen mode.

> **Note:** 320 x 256 is equivalent to a display resolution of 1280 x 1024 at a 400% zoom with the browser set to full-screen mode.

Use `mcp_playwright_browser_run_code` to apply zoom:

```javascript
async (page) => {
  // Apply 400% zoom via CSS transform
  await page.evaluate(() => {
    document.body.style.zoom = '400%';
  });
  return 'Zoom set to 400%';
}
```

Alternatively, set the effective viewport to 320x256 directly:

```javascript
async (page) => {
  // Set viewport to simulate 400% zoom (1280/4 = 320, 1024/4 = 256)
  await page.setViewportSize({ width: 320, height: 256 });
  return 'Viewport set to 320x256 (simulating 400% zoom)';
}
```

### Step 3: Capture Full-Page Screenshot

Use `mcp_playwright_browser_take_screenshot` with `fullPage: true` to capture the entire page at the 320px viewport width. This captures all vertically-scrollable content in a single image for visual analysis.

```
mcp_playwright_browser_take_screenshot with:
  - fullPage: true
  - type: "png"
```

### Step 4: Visually Analyze Screenshot for Reflow Issues

Examine the full-page screenshot to identify content that extends beyond the viewport width or is visually clipped. Look for these visual indicators:

#### Signs of Horizontal Overflow in Screenshots

1. **Text cut off at the right or left edge**
   - Words or characters that appear sliced at the viewport boundary
   - Text that runs off the visible area
   - Long URLs or strings that don't wrap

2. **Content extending beyond viewport**
   - Elements visually protruding past the right edge
   - Elements visually getting cut off at the left edge
   - Buttons, links, or controls partially visible
   - Navigation items cut off mid-word

3. **Clipped or hidden content**
   - Text that appears truncated without "..." indicators
   - Content that seems incomplete compared to expected page structure
   - Gaps where content should logically appear

4. **Layout issues**
   - Overlapping text or elements
   - Content rendered on top of other content
   - Unreadable text due to overlap or clipping

#### Contextual Exception Analysis

For each visual overflow issue identified, determine whether it falls under an allowed exception:

| Exception Type | Visual Indicators in Screenshot |
|----------------|--------------------------------|
| **Data tables** | Tabular layouts with rows/columns, header cells, data grids |
| **Images/graphics** | Photos, illustrations, icons, decorative graphics |
| **Video** | Video players, embedded media with playback controls |
| **Maps** | Geographic maps, location pins, zoom controls, map tiles |
| **Diagrams** | Flowcharts, org charts, technical drawings, schematics |
| **Games** | Interactive game areas, score displays, game controls |
| **Presentations** | Slide layouts, presentation controls, fixed-dimension content |
| **Toolbar interfaces** | Persistent toolbars, formatting controls, editing interfaces |

#### Visual Verification Checklist

Scroll through the entire screenshot and verify:

1. **Identify the content type** from the snapshot's accessibility tree
2. **Check if it's an exception** using the indicators above
3. **If excepted**: Verify that surrounding content (headings, paragraphs, navigation) still appears properly formatted in the snapshot
4. **If NOT excepted**: This is a reflow failure

> **Key Principle:** The exception only applies to the specific element requiring two-dimensional layout. Even when exception content is present, surrounding text content must still reflow properly within the 320px viewport.

### Step 5: Record Results

Record your results:

- Select **Fail** for any instances where:
  - Text content requires horizontal scrolling to read
  - Content is cut off or truncated without an accessible way to view it
  - Content is clipped on the LEFT side (first characters missing)
  - Content is clipped on the RIGHT side without ellipsis or alternative access
  - Users must scroll horizontally to read lines of text
  
- Select **Pass** if:
  - All text content reflows to fit within the viewport
  - Only exception content (tables, images, maps, etc.) requires horizontal scrolling
  - Content is accessible through alternative means (expandable sections, links, tooltips, etc.)
  - Truncated content has visible ellipsis AND an alternative way to access full content

## Pass/Fail Criteria

### ✅ Pass

- **Content reflows properly:** All non-excepted content fits within the 320 CSS pixel viewport width at 400% zoom
- **No horizontal scrolling for text:** Users can read all text by scrolling vertically only
- **Exceptions contained:** Excepted content (data tables, images, video, maps, diagrams, games, presentations, toolbar interfaces) may extend beyond viewport, but is ideally contained in its own scrollable region
- **Surrounding content reflows:** Content around excepted elements (headings, paragraphs, navigation, pagination) still reflows properly
- **Individual cells reflow:** Within data tables, individual cell content fits within the viewport (the table structure is excepted, not the cell contents)

### ❌ Fail

Record as **FAIL** if:

- **Horizontal scrolling required for text:** Users must scroll horizontally to read lines of text in non-excepted content
- **Left-side clipping:** Content is cut off on the left edge due to negative positioning or ancestor overflow (e.g., "Resource group" appears as "esource group")
- **Right-side clipping without alternative:** Content is cut off on the right without ellipsis, tooltip, or copy button to access full content
- **Exception extends to non-excepted content:** A data table causes the entire page (including paragraphs) to require horizontal scrolling
- **Content disappears:** Text or functionality is lost when content reflows (see [F102](https://www.w3.org/WAI/WCAG21/Techniques/failures/F102))
- **Fixed-width layouts:** Page uses fixed pixel widths that don't adapt to viewport size
- **Text overlaps:** Content becomes unreadable due to overlap or clipping

## References

### WCAG Success Criteria

- [Understanding Success Criterion 1.4.10: Reflow](https://www.w3.org/WAI/WCAG21/Understanding/reflow.html)

### Sufficient Techniques

- [Using CSS Flexbox to reflow content](https://www.w3.org/WAI/WCAG21/Techniques/css/C31)
- [Using media queries and grid CSS to reflow columns](https://www.w3.org/WAI/WCAG21/Techniques/css/C32)
- [Allowing for Reflow with Long URLs and Strings of Text](https://www.w3.org/WAI/WCAG21/Techniques/css/C33)
- [Using CSS width, max-width and flexbox to fit labels and inputs](https://www.w3.org/WAI/WCAG21/Techniques/css/C38)
- [Calculating size and position in a way that scales with text size](https://www.w3.org/WAI/WCAG21/Techniques/client-side-script/SCR34)

### Additional Guidance

- [Examples – Responsive Web Design](https://www.w3.org/WAI/WCAG21/Techniques/general/G206)
