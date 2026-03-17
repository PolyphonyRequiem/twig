# Reflow Report

<!--
## EXAMPLE REPORT

## Results

| Test | Result |
|------|--------|
| 1.4.10: Reflow | ❌ FAIL |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| horizontal-scroll | Content requires horizontal scrolling at 320px width | `div.hero-banner` | 1.4.10 | Use responsive CSS: `max-width: 100%` and flexible layouts |
| fixed-width | Element has fixed width causing overflow | `table.pricing-table` | 1.4.10 | Convert to responsive table or card layout on mobile |
| overflow-hidden | Content clipped and inaccessible at 320px | `nav.main-menu` | 1.4.10 | Implement collapsible mobile menu |

---

## Exceptions

| Element | Exception Type | Selector | Location | Notes |
|---------|---------------|----------|----------|-------|
| Sales Data Table | Data Table | `table.sales-grid` | Dashboard | Complex data table - horizontal scroll acceptable |
| Site Map Image | Image | `img.campus-map` | Contact page | Map requires 2D scrolling by nature |

---

## Notes

- **div.hero-banner (horizontal-scroll)**: Hero banner has `width: 1200px` fixed. At 320px viewport, content overflows requiring horizontal scroll. Change to `max-width: 100%` and use responsive background image or flexbox layout.
- **table.pricing-table (fixed-width)**: Pricing comparison table has 5 columns with fixed widths totaling 800px. At 320px viewport, table overflows. Convert to stacked card layout on mobile using CSS Grid or display as definition list.
- **nav.main-menu (overflow-hidden)**: Navigation menu has `overflow: hidden` and items are clipped at 320px. Some menu items completely invisible and inaccessible. Implement hamburger menu pattern for mobile viewports.
- **Exceptions**: Data tables (table.sales-grid) and maps (img.campus-map) are exempt from reflow requirements per WCAG 1.4.10 as they require two-dimensional layout for meaning.
- Tested at 320px viewport width (equivalent to 400% zoom on 1280px display).
-->

## Results

| Test | Result |
|------|--------|
| 1.4.10: Reflow | {✅ PASS / ❌ FAIL} |

---

## Violations

| Rule | Description | Element | WCAG | Fix |
|------|-------------|---------|------|-----|
| {rule-id} | {help text} | `{selector}` | {criterion} | {recommendation} |

---

## Exceptions

| Element | Exception Type | Selector | Location | Notes |
|---------|---------------|----------|----------|-------|
| {element-name} | {Data Table / Image / Video / Map / Diagram / Game / Presentation / Toolbar} | `{selector}` | {page-location} | {implementation-notes} |

## Notes

{Any additional observations}
