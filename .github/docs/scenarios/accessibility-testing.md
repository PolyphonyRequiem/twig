# Accessibility Testing

Run comprehensive automated accessibility audits on web pages using the Playwright MCP server. Test for WCAG compliance and other accessibility barriers across 11 different testing methodologies.

## When to Use

- Validating WCAG 2.0/2.1/2.2 compliance (Level A, AA, AAA)
- Pre-deployment accessibility audits before releasing features
- Testing specific accessibility criteria (headings, links, images, contrast, keyboard navigation, etc.)
- Generating accessibility reports

## Prerequisites

- **MCP Server**: Playwright MCP server installed and configured
- **URL**: Valid web page URL accessible from your machine

## Workflows

### 1. Run Accessibility Tests

Execute one or more accessibility tests on a target URL.

**Steps:**
1. Invoke `/Octane.A11yTester.Run` in GitHub Copilot Chat
2. Provide the **URL** of the web page to test
3. Optionally provide a **CSS selector** to test a specific element or iframe (e.g., `#main-content`)
4. Select one or more **test types** from the Testing Library
5. Octane.A11yTester agent will navigate to the URL, execute tests, and generate a report

**Expected Output:**
A structured accessibility report based on the selected test types, including:
- Violations or failures found
- WCAG success criteria references
- Element selectors for each issue
- Remediation guidance

### 2. Available Test Types

Select one or more tests from the Testing Library:

#### Automated WCAG Scanning
- **Axe-Core Violations** (`axe-core`)
   - Automated WCAG compliance scanning using axe-core engine
   - Coverage (configurable): WCAG 2.0/2.1/2.2 A/AA/AAA

#### Manual-Style Testing
- **Keyboard Navigation** (`keyboard-navigation`)
   - Tests that users can navigate to all interactive interface components using a keyboard
   - Coverage: WCAG 2.1.1

- **Link Purpose** (`link-purpose`)
   - Tests that the purpose of a link is described by its link text alone, or by the link text together with preceding page context
   - Coverage: WCAG 2.4.4

- **Image Function** (`image-function`)
   - Tests that every image is coded as either meaningful or decorative
   - Coverage: WCAG 1.1.1

- **Focus Order** (`focus-order`)
   - Tests that components receive focus in an order that preserves meaning and operability
   - Coverage: WCAG 2.4.3

- **UI Components Contrast** (`ui-components-contrast`)
   - Tests that visual information used to identify active user interface components and their states have sufficient contrast
   - Coverage: MAS 1.4.3, WCAG 1.4.11

- **No Missing Headings** (`no-missing-headings`)
   - Tests that text that looks like a heading is coded as a heading
   - Coverage: WCAG 1.3.1, 2.4.6

- **Heading Levels** (`heading-levels`)
   - Tests that a heading's programmatic level matches the level that's presented visually
   - Coverage: WCAG 1.3.1

- **Bypass Blocks** (`bypass-blocks`)
   - Tests that a page provides a keyboard-accessible method to bypass repetitive content
   - Coverage: WCAG 2.4.1

- **Instructions** (`instructions`)
    - Tests that if a native widget has a visible label or instructions, they are programmatically determinable
    - Coverage: WCAG 1.3.1, 2.5.3

- **Reflow** (`reflow`)
    - Tests that content is visible without having to scroll in two dimensions
    - Coverage: WCAG 1.4.10

## Example Prompts

```shell
# You'll be asked to provide a URL, an optional target selector, and test selection
/Octane.A11yTester.Run

# You'll be asked to provide an optional target selector and test selection
/Octane.A11yTester.Run https://example.com

# You'll be asked to provide test selection
/Octane.A11yTester.Run https://example.com, entire page

# Run axe-core testing on https://example.com at the scope of TargetA element
/Octane.A11yTester.Run https://example.com, id="TargetA", axe-core
```

## Expected Output
Comprehensive report covering all selected test types with cross-referenced findings and prioritized remediation steps.

## Custom Agents

### Octane.A11yTester

Accessibility testing specialist that orchestrates browser-based accessibility audits. Octane.A11yTester follows strict step-by-step testing protocols defined in skills and generates accurate reports using standardized templates. The agent provides a report on violations found and remediation guidance for those issues.

## Tips and Best Practices

- **Test specific elements** using the CSS selector parameter to focus on components or regions
- **Test specific iframes** - When a web page contains iframes, use a CSS selector to test the iframe content
- **Review by severity** - Address WCAG Level A violations first, then AA, then AAA

## Related Scenarios

- [A11y Bug Fixing](../a11y-bug-fixing/README.md)