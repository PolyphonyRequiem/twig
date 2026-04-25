using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class IconSetTests
{
    private static readonly string[] KnownIconIds =
    [
        "icon_crown", "icon_insect", "icon_check_box", "icon_book",
        "icon_clipboard", "icon_trophy", "icon_gift", "icon_chart",
        "icon_diamond", "icon_list", "icon_test_beaker", "icon_test_plan",
        "icon_test_suite", "icon_test_case", "icon_test_step", "icon_test_parameter",
        "icon_sticky_note", "icon_traffic_cone", "icon_chat_bubble", "icon_flame",
        "icon_megaphone", "icon_code_review", "icon_code_response", "icon_review",
        "icon_response", "icon_star", "icon_ribbon", "icon_headphone",
        "icon_key", "icon_airplane", "icon_car", "icon_asterisk",
        "icon_database_storage", "icon_government", "icon_gavel", "icon_parachute",
        "icon_paint_brush", "icon_palette", "icon_gear", "icon_broken_lightbulb",
        "icon_clipboard_issue",
    ];

    [Fact]
    public void UnicodeIconsByIconId_ContainsAll41Icons()
    {
        IconSet.UnicodeIconsByIconId.Count.ShouldBe(41);
        foreach (var iconId in KnownIconIds)
        {
            IconSet.UnicodeIconsByIconId.ShouldContainKey(iconId);
        }
    }

    [Fact]
    public void NerdFontIconsByIconId_ContainsAll41Icons()
    {
        IconSet.NerdFontIconsByIconId.Count.ShouldBe(41);
        foreach (var iconId in KnownIconIds)
        {
            IconSet.NerdFontIconsByIconId.ShouldContainKey(iconId);
        }
    }

    [Fact]
    public void GetIconByIconId_KnownIconId_ReturnsGlyph()
    {
        IconSet.GetIconByIconId("unicode", "icon_crown").ShouldBe("◆");
        IconSet.GetIconByIconId("nerd", "icon_insect").ShouldBe("\uEAAF");
        IconSet.GetIconByIconId("nerd", "icon_trophy").ShouldBe("\uF091");
    }

    [Fact]
    public void GetIconByIconId_UnknownIconId_ReturnsNull()
    {
        IconSet.GetIconByIconId("unicode", "icon_unknown_future").ShouldBeNull();
        IconSet.GetIconByIconId("nerd", "icon_unknown_future").ShouldBeNull();
    }

    [Fact]
    public void GetIconByIconId_NullIconId_ReturnsNull()
    {
        IconSet.GetIconByIconId("unicode", null).ShouldBeNull();
        IconSet.GetIconByIconId("nerd", null).ShouldBeNull();
    }

    [Fact]
    public void GetIconByIconId_NullMode_FallsBackToUnicode()
    {
        IconSet.GetIconByIconId(null, "icon_crown").ShouldBe("◆");
    }
}
