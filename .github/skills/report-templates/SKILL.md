---
name: report-templates
description: Report templates for accessibility test results. Use these templates to format test output consistently.
---

# Accessibility Report Templates

This skill provides standardized report templates for formatting accessibility test results.

## Available Templates

| Template | Purpose | When to Use |
|----------|---------|-------------|
| [brief-report.md](references/brief-report.md) | Quick summary of violations for a single test | After each individual test completes |
| [comprehensive-report.md](references/comprehensive-report.md) | Full report covering all tests run | At the end of a test session |

### Test-Specific Report Templates

These templates provide detailed violation reporting for each test type:

| Template | Test Type |
|----------|-----------|
| [axe-core-report.md](references/axe-core-report.md) | Axe-Core automated scanning |
| [keyboard-navigation-report.md](references/keyboard-navigation-report.md) | Keyboard navigation |
| [focus-order-report.md](references/focus-order-report.md) | Focus order |
| [link-purpose-report.md](references/link-purpose-report.md) | Link purpose |
| [image-function-report.md](references/image-function-report.md) | Image function |
| [ui-components-contrast-report.md](references/ui-components-contrast-report.md) | UI component contrast |
| [no-missing-headers-report.md](references/no-missing-headers-report.md) | No missing headings |
| [heading-levels-report.md](references/heading-levels-report.md) | Heading levels |
| [bypass-blocks-report.md](references/bypass-blocks-report.md) | Bypass blocks |
| [instructions-report.md](references/instructions-report.md) | Instructions |
| [reflow-report.md](references/reflow-report.md) | Reflow |

## Usage

When generating reports, read the appropriate template from `references/` and populate the placeholders with actual test data.
