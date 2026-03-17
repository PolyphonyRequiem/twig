---
name: keyboard-navigation-testing
description: Use when testing keyboard navigation accessibility (WCAG 2.1.1 Keyboard).
---

# Keyboard Navigation Testing

Test keyboard accessibility to ensure all interactive elements are operable without a mouse.

**WCAG Success Criterion:** 2.1.1 Keyboard (Level A)
- [Understanding Success Criterion 2.1.1: Keyboard](https://www.w3.org/WAI/WCAG21/Understanding/keyboard.html)

---

## Execution Model: Checkpoint-Based Testing

**MANDATORY QUALITY REQUIREMENT:** This test must be performed thoroughly. Do not take shortcuts or sacrifice quality for speed. Every element must be tested, every violation must be investigated, and every result must be documented accurately.

This skill uses **mandatory checkpoints** that MUST be completed in order. Each checkpoint requires:
1. Executing specific actions
2. Recording specific outputs
3. Displaying a checkpoint completion summary before proceeding

**You may NOT proceed to the next checkpoint until the current one is fully complete.**

---

## CRITICAL: Mandatory User Confirmation Gates

**EVERY checkpoint ends with a MANDATORY user confirmation. You MUST:**

1. Display the checkpoint output in the required format
2. Ask the specific confirmation question listed at the end of each checkpoint
3. **WAIT for the user to respond** before proceeding
4. Do NOT combine multiple checkpoints without user confirmation between each

| Checkpoint | Confirmation Question Must Include |
|------------|------------------------------------|
| 1 | "Did I miss any composite widgets?" |
| 2 | "Did I miss any elements or operability failures?" |
| 3 | "Are you satisfied with the depth of testing?" |


**If you skip a confirmation gate, you have violated this skill's protocol.**

---

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

---

## Reference: Keyboard Operability by Element Type

All interactive elements must be **operable** via keyboard, not just navigable. Use the following keystrokes to operate each element type:

| Element Type | Keystroke(s) to Operate | Notes |
|--------------|------------------------|-------|
| **Link** | `Enter` | Operates the link |
| **Button** | `Enter` or `Space` | Either must work for elements with `role="button"` |
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

## CHECKPOINT 1: Widget Inventory

### Actions Required:
1. Take a page snapshot using `mcp_playwright_browser_snapshot`
2. **SCOPING REQUIREMENT**: If a target element was specified:
   - Only examine the section of the snapshot WITHIN the target element
   - Use element ref prefixes to identify target boundaries (e.g., all refs starting with same prefix are inside the target)
   - IGNORE all widgets outside the target, even if they appear in the snapshot
3. Search the snapshot (or target section) for these EXACT role patterns:
   - `menubar` 
   - `menu`
   - `grid`
   - `tree`
   - `tablist`
   - `listbox`
4. For EACH widget found WITHIN SCOPE, count the child items

### Checkpoint 1 Output Required:
```
═══════════════════════════════════════════════════════════
CHECKPOINT 1 COMPLETE: Widget Inventory
─────────────────────────────────────────────────────────────
Composite widgets found: [NUMBER]

WIDGET 1: [role]
  - Location: [describe where in page/iframe]
  - Child items: [NUMBER] [list names if visible]
  - Arrow pattern: [horizontal/vertical/grid]

WIDGET 2: [role]
  - Location: [describe where]
  - Child items: [NUMBER] [list names if visible]
  - Arrow pattern: [horizontal/vertical/grid]

[Continue for ALL widgets...]

If NO composite widgets found, state: "No composite widgets detected"
═══════════════════════════════════════════════════════════
```

**STOP. Display the above output before proceeding to Checkpoint 2.**

### User Confirmation Required

After displaying the checkpoint output, ask the user:

> "**Checkpoint 1 complete.** I found [X] composite widgets. Did I miss any composite widgets you can see on the page? (Reply 'continue' to proceed, or tell me what I missed)"

**Do NOT proceed until the user responds.**

---

## CHECKPOINT 2: Tab Sequence and Operability Testing

**Purpose:** Navigate through ALL focusable elements AND verify they are operable via keyboard in a single pass.

### Actions Required:
1. **SCOPING**: If a target element was specified, testing occurs WITHIN that target only:
   - The first Tab press should already be inside the target (from scoping step)
   - Continue pressing Tab until you exit the target OR return to an already-visited element
   - When focus exits the target boundary, STOP - do not continue testing outside the target

2. **For EACH element in the tab sequence:**
   - Press Tab to focus the element
   - Take a snapshot to verify focus
   - Record the element type and name
   - **Test operability based on element type** (see table below)
   - Record operability result (PASS/FAIL)
   - Restore original state if needed (e.g., uncheck a checkbox, close a dropdown)
   - Continue to next element

### Operability Tests by Element Type:

| Element Type | Test Action | Expected Result |
|--------------|-------------|------------------|
| **link** | Press `Enter` | Navigation occurs OR action triggers (then go back if needed) |
| **button** | Press `Enter`, then `Space` | Both keystrokes operate the button |
| **checkbox** | Press `Space` | State toggles (restore original state after) |
| **textbox** | Type a character, then delete it | Text appears and can be deleted |
| **combobox** | Press `ArrowDown` or `Space` | Dropdown opens (press `Esc` to close) |
| **radio** | Press Arrow keys | Selection moves between options |
| **tab** (in tablist) | Skip - tested in Checkpoint 3 | N/A |
| **menuitem** | Skip - tested in Checkpoint 3 | N/A |

### Execution Pattern:
```
Tab → Snapshot → Record element → Test operability → Record result → Restore state → Repeat
```

### Checkpoint 2 Output Required:
```
═══════════════════════════════════════════════════════════
CHECKPOINT 2 COMPLETE: Tab Sequence and Operability Testing
─────────────────────────────────────────────────────────────
Total elements in tab sequence: [NUMBER]
Elements tested for operability: [NUMBER]

TAB SEQUENCE WITH OPERABILITY RESULTS:
─────────────────────────────────────────────────────────────
#  | Element                        | Type      | Operable?
---|--------------------------------|-----------|------------
1  | [element name]                 | button    | ✅ Enter ✅ Space
2  | [element name]                 | link      | ✅ Enter
3  | [element name]                 | checkbox  | ✅ Space (toggled)
4  | [element name]                 | combobox  | ✅ Opens/closes
5  | [element name]                 | textbox   | ✅ Accepts input
6  | [element name]                 | button    | ❌ Space (no response)
...
[Continue for ALL elements]

─────────────────────────────────────────────────────────────
SUMMARY:
Navigation: [X]/[X] elements reachable (100%)
Operability: [X]/[X] elements operable ([Y] failures)

OPERABILITY FAILURES:
[If any failures, list them here with details]
- #6 [button name]: Space key did not operate (Enter worked)

Focus cycle detected at position: [NUMBER] (returned to element #[X])
═══════════════════════════════════════════════════════════
```

**STOP. Display the above output before proceeding to Checkpoint 3.**

### User Confirmation Required

After displaying the checkpoint output, ask the user:

> "**Checkpoint 2 complete.** I tested [X] elements for navigation and operability with [Y] operability failures. Did I miss any focusable elements, or should I investigate any failures more deeply? (Reply 'continue' to proceed, or tell me what needs attention)"

**Do NOT proceed until the user responds.**

---

## CHECKPOINT 3: Composite Widget Deep Testing

**This checkpoint must be repeated for EACH composite widget identified in Checkpoint 1.**

### IMPORTANT: Thoroughness Over Speed

When testing composite widgets:
- **DO NOT** take shortcuts to save time
- **DO NOT** summarize content (e.g., "and 10 more buttons")
- **DO** Tab through EVERY focusable element
- **DO** record each element individually
- **DO** verify focus actually moved by checking snapshots

**Quality of testing is more important than speed. Incomplete testing defeats the purpose.**

### CHECKPOINT 3A: Menubar Testing (if menubar exists)

#### Actions Required:
1. Tab to focus the menubar
2. Press ArrowRight repeatedly, taking a snapshot after EACH press
3. Continue until focus cycles back to the first item
4. Count total items reached

#### Checkpoint 3A Output Required:
```
═══════════════════════════════════════════════════════════
CHECKPOINT 3A COMPLETE: Menubar Testing
─────────────────────────────────────────────────────────────
Menubar location: [where in page]
Arrow presses to complete cycle: [NUMBER]

ITEMS REACHED (in order):
1. [menuitem name]
2. [menuitem name]
3. [menuitem name]
...
[List ALL items - do not summarize]

Total items: [NUMBER] / [EXPECTED from Checkpoint 1]
Reverse navigation (ArrowLeft): [PASS/FAIL]
Operability (Enter/Space): [PASS/FAIL] - [describe what happened]
═══════════════════════════════════════════════════════════
```

### CHECKPOINT 3B: Tablist Testing (if tablist exists)

#### Actions Required:
1. Use ArrowRight/ArrowLeft to navigate through ALL tabs in the tablist
2. Verify focus moves to each tab sequentially
3. Test wrapping behavior at first and last tabs

Note: The active tab panel's focusable elements are already captured in Checkpoint 2's Tab Sequence Mapping.

#### Checkpoint 3B Output Required:

```
═══════════════════════════════════════════════════════════
CHECKPOINT 3B COMPLETE: Tablist Testing
─────────────────────────────────────────────────────────────
Tablist location: [where in page]
Total tabs: [NUMBER]

Arrow key navigation through all tabs:
  ArrowRight: [PASS/FAIL] - navigated from [first tab] to [last tab]
  ArrowLeft: [PASS/FAIL] - navigated backward successfully
  Wrapping behavior: [wraps/stops at ends]
═══════════════════════════════════════════════════════════
```

### CHECKPOINT 3C: Grid Testing (if grid exists)

#### Actions Required:
1. Tab to focus the grid
2. Navigate ALL column headers with ArrowRight
3. Navigate to data rows with ArrowDown
4. Navigate cells with ArrowRight
5. Test any interactive elements in cells (links, buttons)
6. Navigate back with ArrowUp/ArrowLeft

#### Checkpoint 3C Output Required:
```
═══════════════════════════════════════════════════════════
CHECKPOINT 3C COMPLETE: Grid Testing
─────────────────────────────────────────────────────────────
Grid location: [where in page]

COLUMN HEADERS ([NUMBER] total):
1. [header name]
2. [header name]
...
[List ALL headers]

DATA ROW NAVIGATION:
- Rows accessible: [YES/NO]
- Cells per row: [NUMBER]
- Interactive elements found: [list any links/buttons in cells]

NAVIGATION VERIFICATION:
- ArrowRight through headers: [PASS/FAIL]
- ArrowDown to rows: [PASS/FAIL]
- ArrowUp back to headers: [PASS/FAIL]
═══════════════════════════════════════════════════════════
```

**STOP. Complete ALL applicable 3A/3B/3C checkpoints before proceeding.**

---

### MANDATORY GATE: User Confirmation Required

**STOP - YOU MUST ASK FOR USER CONFIRMATION NOW**

After completing ALL Checkpoint 3 sections, ask the user:

> "**Checkpoint 3 complete.** I tested [X] composite widgets. Are you satisfied with the depth of testing, or should I re-test any widget more thoroughly? (Reply 'continue' to proceed, or tell me what needs more testing)"

**VIOLATION CHECK: Did you ask the above question and wait for a response?**
- If NO → You are violating this skill's protocol. Ask now.
- If YES → You may proceed after the user responds.

**Do NOT proceed to Checkpoint 4 until the user responds.**

---

## CHECKPOINT 4: Final Results

### Checkpoint 4 Output Required:
```
═══════════════════════════════════════════════════════════
CHECKPOINT 4: FINAL RESULTS - Keyboard Navigation Test
═══════════════════════════════════════════════════════════

TEST TARGET: [URL or page description]
DATE: [Current date]

SUMMARY:
─────────────────────────────────────────────────────────────
Tab sequence elements: [NUMBER]
Elements tested for operability: [NUMBER]
Composite widgets tested: [NUMBER]
  - Menubars: [NUMBER] ([X/X] items navigable)
  - Tablists: [NUMBER] ([X/X] tabs tested)
  - Grids: [NUMBER] ([X/X] headers navigable)

NAVIGATION RESULT: [PASS / FAIL]
OPERABILITY RESULT: [PASS / FAIL]
OVERALL RESULT: [PASS / FAIL]
─────────────────────────────────────────────────────────────

[If FAIL, list specific failures:]
NAVIGATION FAILURES:
1. [Element that could not be reached]

OPERABILITY FAILURES:
1. [Element that could not be operated - describe what keystroke failed]

═══════════════════════════════════════════════════════════
```

---

## Pass/Fail Criteria

### ✅ Pass

- 100% of focusable elements reachable via keyboard (Tab + Arrow keys)
- 100% of interactive elements operable via keyboard (Enter, Space, etc.)
- Tab moves between widgets in logical order
- All mouse functionality available via keyboard

### ❌ Fail

- Any focusable element unreachable (navigation failure)
- Any interactive element cannot be operated via keyboard (operability failure)
- Custom control lacks keyboard operability
- Only mouse event handlers used (onclick without onkeydown/onkeyup)

## References

### WCAG success criteria
- [Understanding Success Criterion 2.1.1: Keyboard](https://www.w3.org/WAI/WCAG21/Understanding/keyboard.html)

### Sufficient techniques
- [Ensuring keyboard control for all functionality](https://www.w3.org/WAI/WCAG21/Techniques/general/G202)
- [Using HTML form controls and links](https://www.w3.org/WAI/WCAG21/Techniques/html/H91)
- [Providing keyboard-triggered event handlers](https://www.w3.org/WAI/WCAG21/Techniques/general/G90)
- [Using both keyboard and other device-specific functions](https://www.w3.org/WAI/WCAG21/Techniques/client-side-script/SCR20)
- [Making actions keyboard accessible by using the onclick event of anchors and buttons](https://www.w3.org/WAI/WCAG21/Techniques/client-side-script/SCR35)
- [Using redundant keyboard and mouse event handlers](https://www.w3.org/WAI/WCAG21/Techniques/client-side-script/SCR2)


### Common Failures
- [Failure of Success Criterion 2.1.1 due to using only pointing-device-specific event handlers (including gesture) for a function](https://www.w3.org/WAI/WCAG21/Techniques/failures/F54)
- [Failure of Success Criteria 2.1.1, 2.4.7, and 3.2.1 due to using script to remove focus when focus is received](https://www.w3.org/WAI/WCAG21/Techniques/failures/F55)
- [Failure of Success Criteria 1.3.1, 2.1.1, 2.1.3, or 4.1.2 when emulating links](https://www.w3.org/WAI/WCAG21/Techniques/failures/F42)

### Additional guidance
- [WAI-ARIA Authoring Practices 1.1: Developing a Keyboard Interface](https://www.w3.org/TR/wai-aria-practices/#keyboard)