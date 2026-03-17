#!/usr/bin/env pwsh
# ============================================================================
# Nerd Font Glyph Verification Script
# Prints all 41 NerdFontIconsByIconId glyphs for visual inspection.
# Requires CaskaydiaCove Nerd Font (or any Nerd Font patched font) in terminal.
# ============================================================================

Write-Host ""
Write-Host "=== Nerd Font Glyph Verification ===" -ForegroundColor Cyan
Write-Host "Font requirement: CaskaydiaCove Nerd Font (or compatible NF)" -ForegroundColor Yellow
Write-Host "Each line shows: [glyph]  iconId  (codepoint, nerd-font-name)" -ForegroundColor Yellow
Write-Host "If a glyph shows as a box/rectangle, it is NOT rendering correctly." -ForegroundColor Red
Write-Host ""

$glyphs = [ordered]@{
    "icon_crown"            = @("`u{EB59}",  "U+EB59",  "nf-cod-star_full")
    "icon_insect"           = @("`u{EAAF}",  "U+EAAF",  "nf-cod-bug")
    "icon_check_box"        = @("`u{EAB3}",  "U+EAB3",  "nf-cod-checklist")
    "icon_book"             = @("`u{EAA4}",  "U+EAA4",  "nf-cod-book")
    "icon_clipboard"        = @("`u{EAC0}",  "U+EAC0",  "nf-cod-clippy")
    "icon_trophy"           = @("`u{EB20}",  "U+EB20",  "nf-cod-milestone")
    "icon_gift"             = @("`u{EAF9}",  "U+EAF9",  "nf-cod-gift")
    "icon_chart"            = @("`u{EB03}",  "U+EB03",  "nf-cod-graph")
    "icon_diamond"          = @("`u{F01C8}", "U+F01C8", "nf-md-diamond_stone")
    "icon_list"             = @("`u{EB17}",  "U+EB17",  "nf-cod-list_unordered")
    "icon_test_beaker"      = @("`u{EA79}",  "U+EA79",  "nf-cod-beaker")
    "icon_test_plan"        = @("`u{EBAF}",  "U+EBAF",  "nf-cod-notebook")
    "icon_test_suite"       = @("`u{EB9C}",  "U+EB9C",  "nf-cod-library")
    "icon_test_case"        = @("`u{EA79}",  "U+EA79",  "nf-cod-beaker")
    "icon_test_step"        = @("`u{EB16}",  "U+EB16",  "nf-cod-list_ordered")
    "icon_test_parameter"   = @("`u{EB52}",  "U+EB52",  "nf-cod-settings")
    "icon_sticky_note"      = @("`u{EA7B}",  "U+EA7B",  "nf-cod-file")
    "icon_traffic_cone"     = @("`u{EA6C}",  "U+EA6C",  "nf-cod-warning")
    "icon_chat_bubble"      = @("`u{EA6B}",  "U+EA6B",  "nf-cod-comment")
    "icon_flame"            = @("`u{EAF2}",  "U+EAF2",  "nf-cod-flame")
    "icon_megaphone"        = @("`u{EB1E}",  "U+EB1E",  "nf-cod-megaphone")
    "icon_code_review"      = @("`u{EAE1}",  "U+EAE1",  "nf-cod-diff")
    "icon_code_response"    = @("`u{EAC4}",  "U+EAC4",  "nf-cod-code")
    "icon_review"           = @("`u{EA70}",  "U+EA70",  "nf-cod-eye")
    "icon_response"         = @("`u{EA6B}",  "U+EA6B",  "nf-cod-comment")
    "icon_star"             = @("`u{EB59}",  "U+EB59",  "nf-cod-star_full")
    "icon_ribbon"           = @("`u{EAA5}",  "U+EAA5",  "nf-cod-bookmark")
    "icon_headphone"        = @("`u{F02CE}", "U+F02CE", "nf-md-headset")
    "icon_key"              = @("`u{EB11}",  "U+EB11",  "nf-cod-key")
    "icon_airplane"         = @("`u{EB44}",  "U+EB44",  "nf-cod-rocket")
    "icon_car"              = @("`u{EB44}",  "U+EB44",  "nf-cod-rocket")
    "icon_asterisk"         = @("`u{EA6A}",  "U+EA6A",  "nf-cod-star_empty")
    "icon_database_storage" = @("`u{EACE}",  "U+EACE",  "nf-cod-database")
    "icon_government"       = @("`u{EAC0}",  "U+EAC0",  "nf-cod-clippy")
    "icon_gavel"            = @("`u{EB12}",  "U+EB12",  "nf-cod-law")
    "icon_parachute"        = @("`u{EA6C}",  "U+EA6C",  "nf-cod-warning")
    "icon_paint_brush"      = @("`u{EB2A}",  "U+EB2A",  "nf-cod-paintcan")
    "icon_palette"          = @("`u{EAC6}",  "U+EAC6",  "nf-cod-color_mode")
    "icon_gear"             = @("`u{EAF8}",  "U+EAF8",  "nf-cod-gear")
    "icon_broken_lightbulb" = @("`u{EA61}",  "U+EA61",  "nf-cod-lightbulb")
    "icon_clipboard_issue"  = @("`u{EB0C}",  "U+EB0C",  "nf-cod-issues")
}

$count = 0
foreach ($entry in $glyphs.GetEnumerator()) {
    $count++
    $iconId   = $entry.Key
    $glyph    = $entry.Value[0]
    $codepoint = $entry.Value[1]
    $nfName   = $entry.Value[2]
    Write-Host ("  {0,2}. {1}  {2,-25} ({3}, {4})" -f $count, $glyph, $iconId, $codepoint, $nfName)
}

Write-Host ""
Write-Host "Total: $count glyphs" -ForegroundColor Green
Write-Host ""
Write-Host "=== Verification Checklist ===" -ForegroundColor Cyan
Write-Host "  [ ] All glyphs render as recognizable icons (not boxes/rectangles)"
Write-Host "  [ ] icon_crown (line 1) renders as a star (nf-cod-star_full)"
Write-Host "  [ ] icon_diamond (line 9) renders as a diamond shape (nf-md-diamond_stone)"
Write-Host "  [ ] icon_headphone (line 28) renders as a headset (nf-md-headset)"
Write-Host ""
