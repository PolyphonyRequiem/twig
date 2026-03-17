---
name: focus-order-testing
description: Use when testing focus order accessibility (WCAG 2.4.3 Focus Order).
---

# Focus Order Testing

## Description

Guide for testing focus order accessibility (WCAG 2.4.3 Focus Order).

**WCAG Criterion**: 2.4.3 Focus Order (Level A)
- [Understanding Success Criterion 2.4.3: Focus Order](https://www.w3.org/WAI/WCAG21/Understanding/focus-order.html)

## Why It Matters

When users navigate through a web page, they expect to encounter controls and other content in an order that makes sense and makes it easy to use the page's functionality. Poor focus order can be disorienting to people who use screen readers or screen magnifiers and to people with reading disorders. Poor focus order can also make it difficult or even painful for people who use keyboards because of mobility impairments.

### From a User's Perspective

> "I use a keyboard and a screen reader to navigate content and operate software. When creating solutions, organize content and controls so that I can understand the presentation, meaning and operation of the interface by the order in which information is presented. To 'see' it like I do, write down the text and controls in the order required to complete the task. Next, read the sequence out loud. If it doesn't make sense to you, it is likely to confuse me and many other people."

## Reference: Tab vs Arrow Key Navigation

| Navigation Method | Use For | Examples |
|-------------------|---------|----------|
| **Tab / Shift+Tab** | Moving BETWEEN widgets | Menubar → Searchbox → Filter → Grid |
| **Arrow Keys** | Moving WITHIN composite widgets | Between menubar items, grid cells, tree nodes |

### Composite Widget Types (Use Arrow Keys Inside)

| Role | Arrow Pattern | Example |
|------|---------------|---------|
| `menubar` / `menu` | ArrowLeft/ArrowRight | Command bar items |
| `grid` | All arrows | Data table cells |
| `tree` | ArrowUp/ArrowDown | File tree nodes |
| `tablist` | ArrowLeft/ArrowRight | Tab headers |
| `listbox` | ArrowUp/ArrowDown | Dropdown options |

> ⚠️ **Common Mistake:** Using Tab to navigate within a menubar or grid will SKIP all internal items and move to the next widget. You MUST use arrow keys to reach all items inside composite widgets.

---

## Reference: Keyboard Operability by Element Type

All interactive elements must be **operable** via keyboard, not just navigable. Use the following keystrokes to operate each element type:

| Element Type | Keystroke(s) to Operate | Notes |
|--------------|------------------------|-------|
| **Link** | `Enter` | Operates the link |
| **Button** | `Enter` or `Space` | Both must work for elements with `role="button"` |
| **Checkbox** | `Space` | Toggles checked/unchecked state |
| **Radio button** | `Space` | Selects the focused option; Arrow keys navigate between options |
| **Select/Dropdown** | `Space` (expand), `Enter` (select), `Esc` (collapse) | Arrow keys navigate options |
| **Combobox** | Type to filter, `ArrowUp`/`ArrowDown` to navigate, `Enter` to select | Autocomplete behavior varies |
| **Tab** | `Enter` or `Space` | Selects the tab |
| **Menu item** | `Enter` | Operates the menu item |
| **Slider** | `ArrowUp`/`ArrowDown` or `ArrowLeft`/`ArrowRight` | Changes slider value |
| **Dialog** | `Esc` | Closes the dialog |
| **Tree item** | `Enter` | Operates item; Arrow keys expand/collapse |

---

## Mandatory Execution Steps

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

### Step 1: Navigate and Test Focus Order

Use the keyboard to navigate through all interactive components in the target page, testing focus order as you go:

#### Navigation Keys
1. **Use Tab** to move focus forward between widgets
2. **Use Shift+Tab** to move focus backward between widgets
3. **Use Arrow keys** to navigate within composite widgets (menubars, grids, tabs, trees, listboxes)

For each composite widget:
- Tab lands on **one element** (the container or first item)
- Arrow keys navigate through **all items inside** the widget

#### When You Encounter Trigger Components

If you encounter a trigger that reveals hidden content (e.g., dropdown menus, modal dialogs, expandable sections):

1. **Activate the trigger** (press Enter or Space)
2. **Navigate through the revealed content** using the appropriate keys (Arrow keys for composite widgets)
3. **Close the revealed content** (press Escape or activate a close button)
4. **Verify focus returns to the trigger** - After closing, focus should return to the element that opened the content

> ⚠️ **Common Testing Mistake:** Using Tab inside a dropdown menu will move focus OUT of the menu (expected ARIA behavior). This is NOT a violation. Always use Arrow keys to navigate menu items before testing Escape behavior.

#### Verification Criteria (Check While Navigating)

As you navigate, verify that:

1. **Focus order matches visual order** - Focus moves in a sequence that matches the visual layout (typically left-to-right, top-to-bottom for LTR languages)

   > ⚠️ **Critical:** Do NOT assume DOM order equals visual order. Pages using absolute positioning, CSS transforms, flexbox `order`, or complex table layouts may have DOM order that differs completely from visual presentation. You MUST verify the actual screen position of each focused element (using `getBoundingClientRect()` or screenshots) to confirm focus moves logically across the visual layout.
2. **Related elements are grouped** - Related controls receive focus together (e.g., form fields with their labels)
3. **Modal content traps focus** - When a modal is open, focus stays within the modal until it's closed
4. **Dynamic content is reachable** - Content revealed by triggers is focusable immediately after being revealed
5. **Focus returns logically** - After closing revealed content, focus returns to a logical position (typically the trigger)

### Step 2: Report Tested Elements

After completing navigation testing, display a summary table showing all elements that were tested, including:
- Element description/name
- Element type/role (e.g., button, link, menubar, grid)
- Key action(s) used to navigate to/within the element (Tab, Shift+Tab, Arrow keys)
- Key action(s) used to operate the element (Enter, Space, Escape, etc.)

Example format:
```
| Element | Type/Role | Navigation Key(s) | Operation Key(s) |
|---------|-----------|-------------------|------------------|
| Main menu | menubar | Tab | ArrowLeft/ArrowRight, Enter |
| Search box | textbox | Tab | (typed text) |
| Submit button | button | Tab | Enter/Space |
| Settings dropdown | menu | Tab, ArrowDown | Enter |
```

### Step 3: Confirm Coverage with User

Ask the user explicitly:

> "Based on the elements I tested above, did I miss testing any interactive elements on this page? Please let me know if there are any components I should also test before proceeding."

**⛔ STOP:** Wait for user response before proceeding.

- If the user confirms testing is complete → Proceed to Step 4
- If the user identifies missed elements → Return to Step 1 to test those elements, then repeat Steps 2-3

### Step 4: Record Results

**✅ Pass** if:
- All interactive components receive focus in an order that preserves meaning and operability
- Focus order is logical and matches visual flow - elements receive focus in the order they are intended to be read and interacted with (typically left-to-right, top-to-bottom for LTR languages)
- Revealed content is properly accessible and focus is managed correctly

**❌ Fail** if:
- Focus order is confusing or illogical
- Focus jumps unexpectedly to unrelated areas of the page
- Interactive elements are skipped or unreachable
- Focus is lost or moves to an unexpected location after interacting with triggers
- Modal dialogs don't trap focus
- Focus doesn't return to a logical position after closing revealed content

## References

### WCAG Success Criteria
- [Understanding Success Criterion 2.4.3: Focus Order](https://www.w3.org/WAI/WCAG21/Understanding/focus-order.html)

### Sufficient Techniques
- [Placing the interactive elements in an order that follows sequences and relationships within the content](https://www.w3.org/WAI/WCAG22/Techniques/general/G59)
- [Creating a logical tab order through links, form controls, and objects](https://www.w3.org/WAI/WCAG22/Techniques/html/H4)
- [Making the DOM order match the visual order](https://www.w3.org/WAI/WCAG22/Techniques/css/C27)
- [Inserting dynamic content into the Document Object Model immediately following its trigger element](https://www.w3.org/WAI/WCAG22/Techniques/client-side-script/SCR26)
- [Creating Custom Dialogs in a Device Independent Way](https://www.w3.org/WAI/WCAG22/Techniques/client-side-script/SCR37)
- [Reordering page sections using the Document Object Model](https://www.w3.org/WAI/WCAG22/Techniques/client-side-script/SCR27)

### Common Failures
- [Failure of Success Criterion 2.4.3 due to using tabindex to create a tab order that does not preserve meaning and operability](https://www.w3.org/WAI/WCAG22/Techniques/failures/F44)
- [Failure of Success Criterion 2.4.3 due to using dialogs or menus that are not adjacent to their trigger control in the sequential navigation order](https://www.w3.org/WAI/WCAG22/Techniques/failures/F85)

### Additional Guidance
- [Using JavaScript to trap focus in an element](https://hiddedevries.nl/en/blog/2017-01-29-using-javascript-to-trap-focus-in-an-element)
- [Creating an Accessible Modal Dialog](https://bitsofco.de/accessible-modal-dialog/)
- [Dialog (Modal) in WAI-ARIA Authoring Practices 1.1](https://www.w3.org/TR/wai-aria-practices/)