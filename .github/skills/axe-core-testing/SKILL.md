---
name: axe-core-testing
description: Use when performing automated WCAG compliance scanning using the axe-core accessibility engine.
---

# Axe-Core Accessibility Testing

Automated WCAG compliance scanning using the axe-core accessibility engine.

| Property | Value |
|----------|-------|
| **Engine** | [axe-core 4.10.2](https://github.com/dequelabs/axe-core) |
| **Coverage** | WCAG 2.0/2.1/2.2 (A, AA, AAA) |

## Inputs

### WCAGLevels (Optional)

**Before running the test, ask the user:**

> "Which WCAG conformance levels would you like to test? You can specify one or more from the options below, or press Enter to use the default (all A and AA levels)."

Present the available options:

| Tag | Description |
|-----|-------------|
| `wcag2a` | WCAG 2.0 Level A |
| `wcag2aa` | WCAG 2.0 Level AA |
| `wcag21a` | WCAG 2.1 Level A |
| `wcag21aa` | WCAG 2.1 Level AA |
| `wcag22a` | WCAG 2.2 Level A |
| `wcag22aa` | WCAG 2.2 Level AA |

> **Note:** Tags are NOT cumulative. Each tag only includes criteria specific to that version.

**Default (if user does not specify):** All A and AA levels (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22a`, `wcag22aa`)

If the user provides specific levels, use only those. If the user declines or provides no input, proceed with the default.

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Detect Iframes

Use `mcp_playwright_browser_run_code` to find all iframes:

```javascript
async (page) => {
  const iframes = await page.evaluate(() => {
    const frames = document.querySelectorAll('iframe');
    return Array.from(frames).map((frame, index) => ({
      index,
      selector: frame.id ? `iframe#${frame.id}` : 
                frame.name ? `iframe[name="${frame.name}"]` : 
                `iframe:nth-of-type(${index + 1})`,
      src: frame.src || '(no src)',
      title: frame.title || '(no title)'
    }));
  });
  return JSON.stringify(iframes, null, 2);
}
```

### Step 2: Inject axe-core

```javascript
async (page) => {
  await page.addScriptTag({
    url: 'https://cdnjs.cloudflare.com/ajax/libs/axe-core/4.10.2/axe.min.js'
  });
  await page.waitForFunction(() => typeof window.axe !== 'undefined');
  return 'axe-core injected';
}
```

### Step 3: Run Analysis

**If a target selector was provided, scan the specific element:**

```javascript
async (page) => {
  const selector = '#main-content'; // Replace with target selector
  const wcagTags = WCAGLevels || ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];
  
  const results = await page.evaluate(async (sel, tags) => {
    const element = document.querySelector(sel);
    if (!element) throw new Error(`Element not found: ${sel}`);
    return await window.axe.run(element, {
      runOnly: { type: 'tag', values: tags },
      iframes: false
    });
  }, selector, wcagTags);
  
  return JSON.stringify({
    source: `element: ${selector}`,
    violations: results.violations,
    passes: results.passes
  }, null, 2);
}
```

**If target selector was NOT provided, scan the entire page:**

```javascript
async (page) => {
  const wcagTags = WCAGLevels || ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];
  
  const results = await page.evaluate(async (tags) => {
    return await window.axe.run(document, {
      runOnly: { type: 'tag', values: tags },
      iframes: false
    });
  }, wcagTags);
  
  return JSON.stringify({
    source: 'main-page',
    violations: results.violations,
    passes: results.passes
  }, null, 2);
}
```

### Step 4: Scan Iframes

For each iframe, inject and run axe-core via `contentFrame()`:

```javascript
async (page) => {
  const iframeSelector = 'iframe#example';
  const wcagTags = WCAGLevels || ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];
  
  const frameElement = page.locator(iframeSelector);
  const frameHandle = await frameElement.elementHandle();
  const contentFrame = await frameHandle.contentFrame();
  
  if (!contentFrame) {
    return JSON.stringify({ source: iframeSelector, error: 'Could not access iframe' });
  }
  
  await contentFrame.addScriptTag({
    url: 'https://cdnjs.cloudflare.com/ajax/libs/axe-core/4.10.2/axe.min.js'
  });
  await contentFrame.waitForFunction(() => typeof window.axe !== 'undefined');
  
  const results = await contentFrame.evaluate(async (tags) => {
    return await window.axe.run(document, {
      runOnly: { type: 'tag', values: tags },
      iframes: false
    });
  }, wcagTags);
  
  return JSON.stringify({
    source: `iframe: ${iframeSelector}`,
    violations: results.violations,
    passes: results.passes
  }, null, 2);
}
```

## Error Handling

| Error | Resolution |
|-------|------------|
| Page fails to load | Verify URL is correct and accessible |
| Element not found | Verify CSS selector is valid |
| Iframe inaccessible | Log as "could not be scanned"; continue with other content |
| axe-core injection fails | Try alternative CDN: `https://unpkg.com/axe-core/axe.min.js` |
| Timeout during scan | Increase timeout; report partial results |

## Notes

- Some violations may be false positives—review context before implementing fixes
- For dynamically loaded content, ensure the page is fully rendered before scanning
- Iframes from different origins require direct scanning via `contentFrame()` to avoid CORS restrictions

## References

- [axe-core GitHub](https://github.com/dequelabs/axe-core)
- [axe-core Rule Descriptions](https://github.com/dequelabs/axe-core/blob/develop/doc/rule-descriptions.md)