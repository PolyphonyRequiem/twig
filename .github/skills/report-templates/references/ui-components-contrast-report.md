# UI Component Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 1.4.11 Non-text Contrast (Level AA) | ❌ FAIL |

---

## Violations

| Element | Issue | Fix |
|---------|-------|-----|
| Text input `input.form-control` | Border contrast ratio 2.1:1 (required: 3:1) | Darken border from #CCCCCC to #767676 |
| Checkbox `input[type="checkbox"]` | Unchecked state contrast ratio 2.5:1 | Increase border contrast to at least 3:1 |
| Focus indicator on buttons | Focus ring contrast ratio 1.8:1 | Use darker focus color (#0066CC instead of #66AAFF) |
| Custom slider thumb | Thumb color contrast 2.3:1 against track | Darken thumb color or add visible border |

---

## Notes

- **input.form-control (border contrast)**: Text input borders use #CCCCCC on white background = 1.6:1 ratio. Users with low vision cannot distinguish the input boundaries. Darken border to #767676 (4.5:1) or #949494 (3:1 minimum).
- **input[type="checkbox"] (unchecked state)**: Unchecked checkbox border is #AAAAAA on white = 2.3:1. The checkbox boundary is not perceivable. Need 3:1 minimum. Darken to #767676.
- **Focus indicator on buttons**: Focus ring uses #66AAFF (light blue) on white background = 1.8:1. Users cannot see which element has focus. Use #0066CC (4.5:1) or add additional focus indicator like outline offset.
- **Custom slider thumb**: Volume slider thumb is #BBBBBB on #EEEEEE track = 1.3:1. Thumb is barely visible. Darken thumb to #767676 or add 1px dark border.
- UI components require 3:1 contrast ratio against adjacent colors.
-->

## Results

| Test | Result |
|------|--------|
| 1.4.11 Non-text Contrast (Level AA) | {✅ PASS / ❌ FAIL} |

---

## Violations

| Element | Issue  | Fix |
|---------|-------|------|
| {element name or description} | {description} | {recommendation} |

---

## Notes
