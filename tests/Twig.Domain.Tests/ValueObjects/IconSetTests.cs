using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class IconSetTests
{
    private static readonly string[] KnownTypeNames =
    [
        "Epic", "Feature", "User Story", "Product Backlog Item",
        "Requirement", "Bug", "Task", "Impediment", "Risk",
        "Issue", "Test Case", "Change Request", "Review",
    ];

    [Fact]
    public void UnicodeIcons_ContainsAllKnownTypes()
    {
        IconSet.UnicodeIcons.Count.ShouldBe(13);
        foreach (var typeName in KnownTypeNames)
        {
            IconSet.UnicodeIcons.ShouldContainKey(typeName);
        }
    }

    [Fact]
    public void NerdFontIcons_ContainsAllKnownTypes()
    {
        IconSet.NerdFontIcons.Count.ShouldBe(13);
        foreach (var typeName in KnownTypeNames)
        {
            IconSet.NerdFontIcons.ShouldContainKey(typeName);
        }

        // Crown glyph fix: Epic now uses nf-cod-star_full (U+EB59)
        IconSet.NerdFontIcons["Epic"].ShouldBe("\ueb59");
    }

    [Fact]
    public void GetIcons_Unicode_ReturnsUnicodeIcons()
    {
        IconSet.GetIcons("unicode").ShouldBeSameAs(IconSet.UnicodeIcons);
    }

    [Fact]
    public void GetIcons_Nerd_ReturnsNerdFontIcons()
    {
        IconSet.GetIcons("nerd").ShouldBeSameAs(IconSet.NerdFontIcons);
    }

    [Fact]
    public void GetIcons_UnknownMode_FallsBackToUnicode()
    {
        IconSet.GetIcons("emoji").ShouldBeSameAs(IconSet.UnicodeIcons);
    }

    [Fact]
    public void GetIcon_KnownType_ReturnsIcon()
    {
        IconSet.GetIcon(IconSet.UnicodeIcons, "Epic").ShouldBe("◆");
    }

    [Fact]
    public void GetIcon_UnknownType_ReturnsDefaultIcon()
    {
        IconSet.GetIcon(IconSet.UnicodeIcons, "SomeNewType").ShouldBe("·");
    }

    [Fact]
    public void GetIcon_CaseInsensitive()
    {
        IconSet.GetIcon(IconSet.UnicodeIcons, "epic").ShouldBe("◆");
    }

    [Fact]
    public void GetIcons_NullMode_FallsBackToUnicode()
    {
        IconSet.GetIcons(null!).ShouldBeSameAs(IconSet.UnicodeIcons);
    }

    [Fact]
    public void GetIcon_NullTypeName_ReturnsDefaultIcon()
    {
        IconSet.GetIcon(IconSet.UnicodeIcons, null!).ShouldBe("·");
    }

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
        IconSet.GetIconByIconId("nerd", "icon_insect").ShouldBe("\ueaaf");
        IconSet.GetIconByIconId("nerd", "icon_trophy").ShouldBe("\udb81\udd3b");
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
