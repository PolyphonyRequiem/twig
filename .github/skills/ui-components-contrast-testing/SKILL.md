---
name: ui-components-contrast-testing
description: Use when testing UI component contrast accessibility against MAS 1.4.3 (4.5:1) or WCAG 1.4.11 (3:1) Non-text Contrast requirements.
---

# UI Component Contrast Testing

## Description

Guide for testing UI component contrast accessibility against MAS 1.4.3 and WCAG 1.4.11 Non-text Contrast requirements.

**WCAG Criterion**: 1.4.11 Non-text Contrast (Level AA) — requires **3:1** for non-text UI components
- [Understanding Success Criterion 1.4.11: Non-text Contrast](https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast.html)

**MAS 1.4.3 (Microsoft Accessibility Standard)** — requires **4.5:1** for UI components (stricter than WCAG)

> **Which standard applies?** MAS is the internal Microsoft standard and is more stringent. When testing Microsoft products, apply the **4.5:1** MAS threshold. When testing for WCAG-only compliance, the minimum is **3:1**.

## Why It Matters

Visual information used to indicate states and boundaries of active UI Components must have sufficient contrast. Most people find it easier to see and use UI Components when they have sufficient contrast against the background. People with low vision, limited color perception, or presbyopia are especially likely to struggle with controls when contrast is too low.

### From a User's Perspective

> "When buttons, form fields, and other controls don't have enough contrast, I can't tell where to click or what state they're in. A subtle border or faint icon might be invisible to me, making the interface unusable."

## Key Concepts

### Contrast Ratio Requirements by Standard

| Standard | Required Ratio | Scope |
|----------|---------------|-------|
| WCAG 1.4.11 (Level AA) | 3:1 | Minimum for WCAG compliance |
| MAS 1.4.3 (Microsoft) | 4.5:1 | Required for Microsoft products |

Visual information used to identify active UI components and their states must meet the applicable threshold against the adjacent background. When testing Microsoft products, apply the **4.5:1** MAS requirement:

- Any visual information that's needed to identify the component
  - Visual information is almost always needed to identify text inputs, checkboxes, and radio buttons.
  - Visual information might not be needed to identify other components if they are identified by their position, text style, or context.
- Any visual information that indicates the component is in its normal state

### Exceptions

No minimum contrast ratio is required if either of the following is true:
- The component is inactive/disabled
- The component's appearance is determined solely by the browser (user agent)

### State Changes Clarification

This success criterion does not require that changes in color that differentiate between states of an individual component meet the applicable contrast ratio threshold (4.5:1 for MAS or 3:1 for WCAG) when they do not appear next to each other. For example, there is not a new requirement that visited links contrast with the default color, or that mouse hover indicators contrast with the default state.

However, the component must not lose contrast with the adjacent colors, and non-text indicators such as the check in a checkbox, or an arrow graphic indicating a menu is selected or open must have sufficient contrast to the adjacent colors.

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 0: Confirm Applicable Standard

> **⛔ MANDATORY CHECKPOINT — YOU MUST STOP HERE AND WAIT FOR THE USER'S RESPONSE BEFORE PROCEEDING TO STEP 1.**

**Before testing, ask the user:**

> "Which accessibility standard should I apply for contrast testing?
> - **MAS** (Microsoft Accessibility Standard 1.4.3) — 4.5:1 minimum, required for Microsoft products
> - **WCAG** (1.4.11 Level AA) — 3:1 minimum, for general WCAG compliance"

**Do NOT proceed to Step 1 until the user has responded.** Do not assume the answer based on the URL, domain, or any other context. Wait for explicit user input.

**Default (if user responds but does not specify a preference):** MAS (4.5:1)

### Step 1: Identify UI Components

In the target page, identify all active user interface components in their states, including:
- Buttons and links
- Form controls (text inputs, checkboxes, radio buttons, dropdowns)
- Custom widgets (toggles, sliders, tabs)
- Interactive icons

Examine each component in its **normal state** (not disabled, no mouseover or input focus).

### Step 2: Measure Contrast Ratios

Verify contrast ratios:

1. **Identify the visual boundary or indicator** of the component
2. **Sample the foreground color** (the border, icon, or state indicator)
3. **Sample the adjacent background color**
4. **Calculate the contrast ratio** — must be at least **4.5:1** (MAS) or **3:1** (WCAG minimum)

### Step 3: Record Results

**✅ Pass (MAS)** if:
- All visual information needed to identify UI components has at least 4.5:1 contrast
- All visual state indicators have at least 4.5:1 contrast
- Focus indicators are clearly visible with at least 4.5:1 contrast

**✅ Pass (WCAG only)** if:
- All of the above meet at least 3:1 contrast

**❌ Fail (MAS)** if:
- Component boundaries have less than 4.5:1 contrast
- Icons or indicators used to identify components have less than 4.5:1 contrast
- State indicators (focus, hover, selected) have less than 4.5:1 contrast

**❌ Fail (WCAG)** if:
- Any of the above have less than 3:1 contrast

## References

### WCAG Success Criteria
- [Understanding Success Criterion 1.4.11: Non-text Contrast](https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast.html)

### Sufficient Techniques
- [Using an author-supplied, highly visible focus indicator](https://www.w3.org/WAI/WCAG22/Techniques/general/G195)

### Common Failures
- [Failure of Success Criterion 2.4.7 due to styling element outlines and borders in a way that removes or renders non-visible the visual focus indicator](https://www.w3.org/WAI/WCAG22/Techniques/failures/F78)

### Additional Guidance
- [Using a contrast ratio of 3:1 with surrounding text and providing additional visual cues on focus for links or controls where color alone is used to identify them](https://www.w3.org/WAI/WCAG21/Techniques/general/G183)