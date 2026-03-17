---
name: link-purpose-testing
description: Use when testing link purpose accessibility (WCAG 2.4.4 Link Purpose In Context).
---

# Link Purpose Testing

Test that the purpose of each link can be determined from the link text alone, or from the link text together with its programmatically determined context.

**WCAG Success Criterion:** 2.4.4 Link Purpose (In Context) (Level A)
- [Understanding Success Criterion 2.4.4: Link Purpose (In Context)](https://www.w3.org/WAI/WCAG21/Understanding/link-purpose-in-context.html)

## Why It Matters

Understanding a link's purpose helps users decide whether they want to follow it. When the link text alone is unclear, sighted users can examine the surrounding context for clues about the link's purpose. Assistive technologies can similarly help non-sighted users by reporting the link's programmatically related context.

### From a User's Perspective

> *"I utilize a screen reader and keyboard to enjoy content and operate software. For every link, provide unique text so I know what will happen if I click the link. For example, 'Shop Devices', 'Shop Software', 'Shop Games'."*

## Key Concepts

### What Makes a Link Purpose Clear?

A link's purpose is **clear** when users can determine:
- Where the link will navigate to
- What action will occur when clicked

### Programmatically Related Context

If the link text alone is unclear, the purpose can be determined from **programmatically related context**, which includes:

| Context Type | Description |
|--------------|-------------|
| **Same sentence** | Text in the same sentence, paragraph, list item, or table cell as the link |
| **Parent list item** | Text in the parent `<li>` element |
| **Table header** | Text in the table header cell associated with the cell containing the link |
| **Preceding text** | Text that immediately precedes the link |

### Link Text Best Practices

| Quality | Approach |
|---------|----------|
| **Best** | Descriptive link text (e.g., "Download annual report PDF") |
| **Better** | Accessible name via `aria-label` or `aria-labelledby` |
| **Good** | Link text + programmatically related context |

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

### Step 2: Identify All Links

Use `mcp_playwright_browser_run_code` to find all links in the target:

```javascript
async (page) => {
  const links = await page.evaluate(() => {
    const allLinks = document.querySelectorAll('a[href], [role="link"]');
    return Array.from(allLinks).map((link, index) => ({
      index,
      text: link.textContent?.trim().substring(0, 100) || '',
      ariaLabel: link.getAttribute('aria-label') || '',
      href: link.getAttribute('href')?.substring(0, 80) || '',
      title: link.getAttribute('title') || '',
      selector: link.id ? `#${link.id}` : `a:nth-of-type(${index + 1})`
    }));
  });
  return JSON.stringify(links, null, 2);
}
```

### Step 3: Evaluate Each Link's Accessible Name

For each link, determine its accessible name using this priority:
1. `aria-labelledby` (references other elements)
2. `aria-label` attribute
3. Link text content (including `alt` text of images inside the link)
4. `title` attribute (last resort)

```javascript
async (page) => {
  const links = await page.evaluate(() => {
    const allLinks = document.querySelectorAll('a[href], [role="link"]');
    return Array.from(allLinks).map((link, index) => {
      // Get accessible name
      let accessibleName = '';
      
      // 1. aria-labelledby
      const labelledBy = link.getAttribute('aria-labelledby');
      if (labelledBy) {
        const labelEl = document.getElementById(labelledBy);
        if (labelEl) accessibleName = labelEl.textContent?.trim();
      }
      
      // 2. aria-label
      if (!accessibleName) {
        accessibleName = link.getAttribute('aria-label') || '';
      }
      
      // 3. Text content (including alt text)
      if (!accessibleName) {
        const img = link.querySelector('img');
        const imgAlt = img ? img.getAttribute('alt') : '';
        accessibleName = link.textContent?.trim() || imgAlt || '';
      }
      
      // 4. title
      if (!accessibleName) {
        accessibleName = link.getAttribute('title') || '';
      }
      
      // Get context
      const parent = link.closest('li, td, p, div');
      const parentText = parent ? parent.textContent?.trim().substring(0, 150) : '';
      
      return {
        index,
        accessibleName: accessibleName.substring(0, 100),
        href: link.getAttribute('href')?.substring(0, 60),
        context: parentText.substring(0, 100),
        hasDescriptiveName: accessibleName.length > 0 && 
          !['click here', 'here', 'read more', 'learn more', 'more', 'link'].includes(accessibleName.toLowerCase())
      };
    });
  });
  return JSON.stringify(links, null, 2);
}
```

### Step 4: Check for Duplicate Link Text

**Run this automated check to identify links with same text but different destinations:**

```javascript
async (page) => {
  const links = await page.evaluate(() => {
    const allLinks = document.querySelectorAll('a[href]');
    const linkMap = {};
    
    Array.from(allLinks).forEach(link => {
      const text = link.textContent?.trim().toLowerCase() || '';
      const href = link.getAttribute('href');
      if (!linkMap[text]) linkMap[text] = [];
      linkMap[text].push(href);
    });
    
    // Find duplicates with different destinations
    const duplicates = {};
    for (const [text, hrefs] of Object.entries(linkMap)) {
      const uniqueHrefs = [...new Set(hrefs)];
      if (uniqueHrefs.length > 1 && text.length > 0) {
        duplicates[text] = uniqueHrefs;
      }
    }
    
    return duplicates;
  });
  return JSON.stringify(links, null, 2);
}
```

- If duplicates are found → Mark each as ❌ **FAIL**
- Links with **different destinations** should have **different link text**
- Links with the **same destination** should have the **same link text**

### Step 5: Evaluate Each Link's Purpose

For each link from Step 3, examine whether its accessible name describes its purpose:

1. **If the link's purpose IS clear from its accessible name → Mark as ✅ PASS**
   - If a link navigates to a document or web page, the name of the document or page is sufficient
   - Example: "Download Annual Report" clearly describes the destination

2. **If the link's purpose is NOT clear from its accessible name → Proceed to Step 7**

### Step 6: Verify Context Provides Purpose

> ⚠️ **Only perform this step for links that did NOT pass in Step 6.**

If a link's purpose is not clear from its accessible name, examine the link in the context of the target page to verify that its purpose is described by the link together with its **preceding page context**.

**Check these context types:**

| Context Type | What to Check |
|--------------|---------------|
| **Same sentence/paragraph** | Text in the same sentence, paragraph, list item, or table cell as the link |
| **Parent list item** | Text in a parent `<li>` element |
| **Table header** | Text in the table header cell that's associated with the cell containing the link |

### Step 7: Record Results

**For each link, record the result:**

| Condition | Result |
|-----------|--------|
| Purpose IS clear from accessible name alone | ✅ **PASS** |
| Purpose IS clear from link + context together | ✅ **PASS** (note: relies on context) |
| Purpose is NOT clear even with context | ❌ **FAIL** |
| Link has no accessible name | ❌ **FAIL** |
| Same text, different destinations (from Step 4) | ❌ **FAIL** |

## Pass/Fail Criteria

### ✅ Pass

- Link text alone clearly describes the link's purpose, OR
- Link text + programmatically related context together describe the purpose
- Links with the same text go to the same destination
- All links have a non-empty accessible name

### ❌ Fail

- Link has no accessible name (empty)
- Links with identical text go to different destinations
- Link purpose cannot be determined even with surrounding context

## References

### WCAG success critera
- [Understanding Success Criterion 2.4.4: Link Purpose (In Context)](https://www.w3.org/WAI/WCAG21/Understanding/link-purpose-in-context.html)

### Sufficient techniques
- [Providing link text that describes the purpose of a link for anchor elements](https://www.w3.org/WAI/WCAG21/Techniques/html/H30)
- [Supplementing link text with the title attribute](https://www.w3.org/WAI/WCAG21/Techniques/html/H33)
- [Using aria-labelledby for link purpose](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA7)
- [Using aria-label for link purpose](https://www.w3.org/WAI/WCAG21/Techniques/aria/ARIA8)
- [Identifying the purpose of a link using link text combined with the text of the enclosing sentence](https://www.w3.org/WAI/WCAG21/Techniques/general/G53)
- [Identifying the purpose of a link using link text combined with its enclosing list item](https://www.w3.org/WAI/WCAG21/Techniques/html/H77)
- [Identifying the purpose of a link using link text combined with its enclosing paragraph](https://www.w3.org/WAI/WCAG21/Techniques/html/H78)
- [Identifying the purpose of a link in a data table using the link text combined with its enclosing table cell and associated table header cells](https://www.w3.org/WAI/WCAG21/Techniques/html/H79)
- [Identifying the purpose of a link in a nested list using link text combined with the parent list item under which the list is nested](https://www.w3.org/WAI/WCAG21/Techniques/html/H81)

### Common Failures
- [Failure of Success Criterion 2.4.4 due to providing link context only in content that is not related to the link](https://www.w3.org/WAI/WCAG21/Techniques/failures/F63)
- [Failure of Success Criteria 2.4.4, 2.4.9 and 4.1.2 due to not providing an accessible name for an image which is the only content in a link](https://www.w3.org/WAI/WCAG21/Techniques/failures/F89)

### Additional Guidance
- [Using the Title Attribute to Help Users Predict Where They Are Going](https://www.nngroup.com/articles/title-attribute/)