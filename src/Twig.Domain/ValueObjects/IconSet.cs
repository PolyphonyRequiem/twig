namespace Twig.Domain.ValueObjects;

/// <summary>
/// Provides icon glyph mappings for ADO icon IDs and badge resolution for work item types.
/// Icon ID mappings (UnicodeIconsByIconId / NerdFontIconsByIconId) are keyed by ADO process
/// icon IDs discovered at runtime — not hardcoded type names.
/// </summary>
public static class IconSet
{
    public static IReadOnlyDictionary<string, string> UnicodeIconsByIconId { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["icon_crown"]            = "◆",
            ["icon_insect"]           = "✦",
            ["icon_check_box"]        = "□",
            ["icon_book"]             = "●",
            ["icon_clipboard"]        = "□",
            ["icon_trophy"]           = "★",
            ["icon_gift"]             = "♦",
            ["icon_chart"]            = "▬",
            ["icon_diamond"]          = "◇",
            ["icon_list"]             = "≡",
            ["icon_test_beaker"]      = "□",
            ["icon_test_plan"]        = "□",
            ["icon_test_suite"]       = "□",
            ["icon_test_case"]        = "□",
            ["icon_test_step"]        = "□",
            ["icon_test_parameter"]   = "□",
            ["icon_sticky_note"]      = "▪",
            ["icon_traffic_cone"]     = "⚠",
            ["icon_chat_bubble"]      = "○",
            ["icon_flame"]            = "✦",
            ["icon_megaphone"]        = "◉",
            ["icon_code_review"]      = "◈",
            ["icon_code_response"]    = "◈",
            ["icon_review"]           = "◎",
            ["icon_response"]         = "○",
            ["icon_star"]             = "★",
            ["icon_ribbon"]           = "▪",
            ["icon_headphone"]        = "♪",
            ["icon_key"]              = "▣",
            ["icon_airplane"]         = "►",
            ["icon_car"]              = "►",
            ["icon_asterisk"]         = "✱",
            ["icon_database_storage"] = "▦",
            ["icon_government"]       = "▣",
            ["icon_gavel"]            = "▣",
            ["icon_parachute"]        = "▽",
            ["icon_paint_brush"]      = "▪",
            ["icon_palette"]          = "◈",
            ["icon_gear"]             = "⚙",
            ["icon_broken_lightbulb"] = "✦",
            ["icon_clipboard_issue"]  = "□",
        };

    public static IReadOnlyDictionary<string, string> NerdFontIconsByIconId { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["icon_crown"]            = "\uEB59",   // nf-cod-star_full (U+EB59)
            ["icon_insect"]           = "\uEAAF",   // nf-cod-bug (U+EAAF)
            ["icon_check_box"]        = "\uEAB3",   // nf-cod-checklist (U+EAB3)
            ["icon_book"]             = "\uEAA4",   // nf-cod-book (U+EAA4)
            ["icon_clipboard"]        = "\uEAC0",   // nf-cod-clippy (U+EAC0)
            ["icon_trophy"]           = "\uEB20",   // nf-cod-milestone (U+EB20)
            ["icon_gift"]             = "\uEAF9",   // nf-cod-gift (U+EAF9)
            ["icon_chart"]            = "\uEB03",   // nf-cod-graph (U+EB03)
            ["icon_diamond"]          = "\uDB80\uDDC8",   // nf-md-diamond_stone (U+F01C8) — no nf-cod equiv
            ["icon_list"]             = "\uEB17",   // nf-cod-list_unordered (U+EB17)
            ["icon_test_beaker"]      = "\uEA79",   // nf-cod-beaker (U+EA79)
            ["icon_test_plan"]        = "\uEBAF",   // nf-cod-notebook (U+EBAF)
            ["icon_test_suite"]       = "\uEB9C",   // nf-cod-library (U+EB9C)
            ["icon_test_case"]        = "\uEA79",   // nf-cod-beaker (U+EA79)
            ["icon_test_step"]        = "\uEB16",   // nf-cod-list_ordered (U+EB16)
            ["icon_test_parameter"]   = "\uEB52",   // nf-cod-settings (U+EB52)
            ["icon_sticky_note"]      = "\uEA7B",   // nf-cod-file (U+EA7B)
            ["icon_traffic_cone"]     = "\uEA6C",   // nf-cod-warning (U+EA6C)
            ["icon_chat_bubble"]      = "\uEA6B",   // nf-cod-comment (U+EA6B)
            ["icon_flame"]            = "\uEAF2",   // nf-cod-flame (U+EAF2)
            ["icon_megaphone"]        = "\uEB1E",   // nf-cod-megaphone (U+EB1E)
            ["icon_code_review"]      = "\uEAE1",   // nf-cod-diff (U+EAE1)
            ["icon_code_response"]    = "\uEAC4",   // nf-cod-code (U+EAC4)
            ["icon_review"]           = "\uEA70",   // nf-cod-eye (U+EA70)
            ["icon_response"]         = "\uEA6B",   // nf-cod-comment (U+EA6B)
            ["icon_star"]             = "\uEB59",   // nf-cod-star_full (U+EB59)
            ["icon_ribbon"]           = "\uEAA5",   // nf-cod-bookmark (U+EAA5)
            ["icon_headphone"]        = "\uDB80\uDECE",   // nf-md-headset (U+F02CE) — no nf-cod equiv
            ["icon_key"]              = "\uEB11",   // nf-cod-key (U+EB11)
            ["icon_airplane"]         = "\uEB44",   // nf-cod-rocket (U+EB44)
            ["icon_car"]              = "\uEB44",   // nf-cod-rocket (U+EB44)
            ["icon_asterisk"]         = "\uEA6A",   // nf-cod-star_empty (U+EA6A)
            ["icon_database_storage"] = "\uEACE",   // nf-cod-database (U+EACE)
            ["icon_government"]       = "\uEAC0",   // nf-cod-clippy (U+EAC0)
            ["icon_gavel"]            = "\uEB12",   // nf-cod-law (U+EB12)
            ["icon_parachute"]        = "\uEA6C",   // nf-cod-warning (U+EA6C)
            ["icon_paint_brush"]      = "\uEB2A",   // nf-cod-paintcan (U+EB2A)
            ["icon_palette"]          = "\uEAC6",   // nf-cod-color_mode (U+EAC6)
            ["icon_gear"]             = "\uEAF8",   // nf-cod-gear (U+EAF8)
            ["icon_broken_lightbulb"] = "\uEA61",   // nf-cod-lightbulb (U+EA61)
            ["icon_clipboard_issue"]  = "\uEB0C",   // nf-cod-issues (U+EB0C)
        };

    /// <summary>
    /// Resolves a glyph for an ADO icon ID in the specified mode.
    /// Returns null if iconId is null or not in the dictionary, allowing callers to apply their own fallback.
    /// When mode is null or unrecognized, falls back to Unicode.
    /// </summary>
    public static string? GetIconByIconId(string? mode, string? iconId)
    {
        if (iconId is null)
            return null;

        var dict = string.Equals(mode, "nerd", StringComparison.OrdinalIgnoreCase)
            ? NerdFontIconsByIconId
            : UnicodeIconsByIconId;

        return dict.TryGetValue(iconId, out var icon) ? icon : null;
    }

    /// <summary>
    /// Resolves the badge glyph for a work item type using the full resolution chain:
    /// (1) iconId lookup via <see cref="GetIconByIconId"/> if <paramref name="typeIconIds"/> contains an entry,
    /// (2) hardcoded unicode switch for known ADO types,
    /// (3) first character of <paramref name="typeName"/> for unknown types,
    /// (4) "■" for empty type names.
    /// </summary>
    public static string ResolveTypeBadge(string iconMode, string typeName, Dictionary<string, string>? typeIconIds)
    {
        if (typeIconIds is not null
            && typeIconIds.TryGetValue(typeName, out var iconId))
        {
            var glyph = GetIconByIconId(iconMode, iconId);
            if (glyph is not null)
                return NormalizeBadgeWidth(glyph);
        }

        return typeName.ToLowerInvariant() switch
        {
            "epic" => "◆",
            "feature" => "▪",
            "user story" or "product backlog item" or "requirement" => "●",
            "bug" or "impediment" or "risk" => "✦",
            "task" or "test case" or "change request" or "review" or "issue" => "□",
            _ => typeName.Length > 0
                ? typeName[0].ToString().ToUpperInvariant()
                : "■",
        };
    }

    /// <summary>
    /// Pads BMP PUA nerd font glyphs (U+E000–U+F8FF) with a trailing space so that
    /// Spectre.Console measures them as width 2 (1 glyph + 1 space). Nerd font terminals
    /// render these glyphs as 2 columns, but Spectre’s wcwidth returns 1. The trailing space
    /// makes the measurement closer to reality. Supplementary PUA glyphs (surrogate pairs)
    /// are left as-is — Spectre measures those as 0, which is a deeper Spectre bug.
    /// </summary>
    private static string NormalizeBadgeWidth(string badge)
    {
        if (badge.Length == 1 && badge[0] >= '\uE000' && badge[0] <= '\uF8FF')
            return badge + " ";
        return badge;
    }
}
