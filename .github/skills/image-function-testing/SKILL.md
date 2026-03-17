---
name: image-function-testing
description: Use when testing image function accessibility (WCAG 1.1.1 Non-text Content).
---

# Image Function Testing

Test that every image is coded as either meaningful or decorative. Meaningful images must have descriptive accessible names; decorative images must be hidden from assistive technology.

**WCAG Success Criterion:** 1.1.1 Non-text Content (Level A)
- [Understanding Success Criterion 1.1.1: Non-text Content](https://www.w3.org/WAI/WCAG21/Understanding/non-text-content.html)

## Why It Matters

Screen readers ignore any image coded as decorative, even if it has an accessible name. Unless an image is coded as decorative, screen readers will assume it's meaningful. In an attempt to communicate the image's meaning, they might announce the image's filename.

### From a User's Perspective

> *"When I use a screen reader, I need images that convey information to have descriptive text alternatives so I can understand what the image is showing. But I don't want to hear about decorative images that don't add meaning - those just create noise and slow me down."*

## Key Concepts

### Meaningful vs. Decorative Images

| Image Type | Definition | How to Code |
|------------|------------|-------------|
| **Meaningful** | Conveys information that isn't available through other page content | Must have a descriptive accessible name (`alt`, `aria-label`, `aria-labelledby`, or `title`) |
| **Decorative** | Could be removed from the page with no impact on meaning or function | Must be coded to be ignored by assistive technology |

### How to Determine Image Function

1. **If the image conveys information** that isn't available through other page content → it's **meaningful**
2. **If the image could be removed** from the page with no impact on meaning or function → it's **decorative**

### Coding Techniques by Image Type

#### Meaningful Images

| Element Type | Technique |
|--------------|-----------|
| `<img>` | Add non-empty `alt` attribute describing the image |
| `<input type="image">` | Add non-empty `alt` attribute |
| `<area>` | Add non-empty `alt` attribute |
| Icon fonts / `<svg>` | Add `role="img"` and `aria-label` or `aria-labelledby` |
| CSS background image | Add visible text element that conveys the image's information |

#### Decorative Images

| Element Type | Technique |
|--------------|-----------|
| `<img>`, `<input>`, `<area>` | Add empty alt attribute: `alt=""` |
| Icon fonts / `<svg>` | Add `role="img"` and `aria-hidden="true"` |
| CSS background image | No additional markup needed |

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

### Step 2: Identify All Images and Their Coded Function

Use `mcp_playwright_browser_run_code` to find all images and determine how they are coded:

```javascript
async (page) => {
  const images = await page.evaluate(() => {
    const results = [];
    
    document.querySelectorAll('img, input[type="image"], svg, area, i, [role="img"]').forEach((el, index) => {
      const tagName = el.tagName.toLowerCase();
      
      // Skip SVG definition elements - they are not rendered content
      if (tagName === 'symbol' || el.closest('defs')) {
        return;
      }
      
      // Skip SVGs that only contain defs (gradient/filter definitions)
      if (tagName === 'svg') {
        const children = Array.from(el.children);
        const hasOnlyDefs = children.length > 0 && children.every(c => c.tagName.toLowerCase() === 'defs');
        const rect = el.getBoundingClientRect();
        const isZeroSize = rect.width === 0 || rect.height === 0;
        if (hasOnlyDefs || isZeroSize) {
          return; // Skip definition-only or invisible SVGs
        }
      }
      
      const alt = el.getAttribute('alt');
      const hasAlt = el.hasAttribute('alt');
      const ariaLabel = el.getAttribute('aria-label');
      const ariaLabelledBy = el.getAttribute('aria-labelledby');
      const title = el.getAttribute('title') || el.querySelector?.('title')?.textContent;
      
      // Check if element or any ancestor hides it from assistive technology
      const isHiddenFromAT = !!el.closest('[aria-hidden="true"], [role="presentation"], [role="none"]');
      
      // Determine accessible name
      let accessibleName = '';
      if (ariaLabelledBy) {
        const ids = ariaLabelledBy.split(' ');
        accessibleName = ids.map(id => document.getElementById(id)?.textContent?.trim() || '').join(' ');
      }
      if (!accessibleName && ariaLabel) accessibleName = ariaLabel;
      if (!accessibleName && alt) accessibleName = alt;
      if (!accessibleName && title) accessibleName = title;
      
      // Determine how the image is coded
      const isCodedMeaningful = accessibleName && accessibleName.trim() !== '';
      const isCodedDecorative = (hasAlt && alt === '') || isHiddenFromAT;
      const hasNoCode = !hasAlt && !ariaLabel && !ariaLabelledBy && !isHiddenFromAT;
      
      // Get context
      const parent = el.closest('a, button, figure, li, td, p');
      const parentText = parent?.textContent?.trim().substring(0, 100) || '';
      const isInLink = !!el.closest('a');
      const isInButton = !!el.closest('button');
      
      // Get source info
      let srcInfo = '';
      if (el.src) srcInfo = el.src.split('/').pop()?.substring(0, 40) || '';
      
      results.push({
        index,
        tagName,
        srcInfo,
        accessibleName: accessibleName?.substring(0, 80) || '',
        codedAs: hasNoCode ? 'NO_CODE' : (isCodedDecorative ? 'DECORATIVE' : 'MEANINGFUL'),
        isInLink,
        isInButton,
        context: parentText.substring(0, 80),
        selector: el.id ? `#${el.id}` : `${tagName}:nth-of-type(${index + 1})`
      });
    });
    
    return results;
  });
  
  return JSON.stringify(images, null, 2);
}
```

> **Notes:**
> - If an image has no code to identify it as meaningful or decorative, it will fail an automated check
> - Assistive technologies will ignore any image coded as decorative, even if it has an accessible name

### Step 3: Examine Each Image to Verify Coded Function is Correct

For each image, examine it visually and verify that its coded function matches its actual function:

**a. An image should be coded as *meaningful* if it conveys information that isn't available through other page content.**

> **✓ Verify the following for meaningful images:**
>
> - [ ] Image has a **non-empty** accessible name (`alt`, `aria-label`, `aria-labelledby`, or `title`)
> - [ ] If it's an icon font, `<svg>` image, or CSS background image → also has `role="img"`
> - [ ] If it's a CSS background image → also has a text element that conveys the image's information and is visible when CSS is turned off

**b. An image should be coded as *decorative* if it could be removed from the page with *no* impact on meaning or function.**

> **✓ Verify the following for decorative images:**
>
> - [ ] `<img>`, `<input>`, `<area>` → has an **empty** alt attribute (`alt=""`)
> - [ ] Icon fonts, `<svg>` → has `role="img"` **and** `aria-hidden="true"`
> - [ ] CSS background images → no additional markup needed

### Step 4: Record Results

**For each image, record your results:**

1. **Select Fail** for any image that does not pass the verification checks in Step 3 and Step 4
2. **Otherwise, select Pass**

## References

### WCAG success criteria
- [Understanding Success Criterion 1.1.1: Non-text Content](https://www.w3.org/WAI/WCAG21/Understanding/non-text-content.html)

### Common Failures

- [Failure of Success Criterion 1.1.1 due to using CSS to include images that convey important information](https://www.w3.org/WAI/WCAG21/Techniques/failures/F3)
- [Failure of Success Criterion 1.1.1 due to not marking up decorative images in HTML in a way that allows assistive technology to ignore them](https://www.w3.org/WAI/WCAG21/Techniques/failures/F38)
- [Failure of Success Criterion 1.1.1 due to providing a text alternative that is not null (e.g., alt="spacer" or alt="image") for images that should be ignored by assistive technology](https://www.w3.org/WAI/WCAG21/Techniques/failures/F39)
- [Failure of Success Criterion 1.1.1 due to omitting the alt attribute or text alternative on img elements, area elements, and input elements of type "image"](https://www.w3.org/WAI/WCAG21/Techniques/failures/F65)
- [Failure of Success Criterion 1.1.1 due to using text look-alikes to represent text without providing a text alternative](https://www.w3.org/WAI/WCAG21/Techniques/failures/F71)
- [Failure of Success Criterion 1.1.1 due to using ASCII art without providing a text alternative](https://www.w3.org/WAI/WCAG21/Techniques/failures/F72)

### Sufficient Techniques

#### For Meaningful Images
- [Providing short text alternative for non-text content that serves the same purpose and presents the same information as the non-text content](https://www.w3.org/WAI/WCAG21/Techniques/general/G94)
- [Using aria-label to provide labels for objects](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA6)
- [Using aria-labelledby to provide a text alternative for non-text content](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA10)
- [Using a text alternative on one item within a group of images that describes all items in the group](https://www.w3.org/WAI/WCAG21/Techniques/general/G196)
- [Combining adjacent image and text links for the same resource](https://www.w3.org/WAI/WCAG21/Techniques/html/H2)
- [Using alt attributes on img elements](https://www.w3.org/WAI/WCAG21/Techniques/html/H37)
- [Providing text alternatives for ASCII art, emoticons, and leetspeak](https://www.w3.org/WAI/WCAG21/Techniques/html/H86)

#### For Decorative Images
- [H67: Using null alt text and no title attribute for decorative images](https://www.w3.org/WAI/WCAG21/Techniques/html/H67)
- [C9: Using CSS to include decorative images](https://www.w3.org/WAI/WCAG21/Techniques/css/C9)
