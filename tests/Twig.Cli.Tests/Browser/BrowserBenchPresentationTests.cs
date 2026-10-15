using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console;
using Twig.Browser;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserBenchPresentationTests
{
    private static readonly Regex Sgr = new("\x1b\\[[0-9;]*m");

    [Fact]
    public void CustomAppearanceUsesNativeBadgeAndUserColorWithoutReadingMutableMetadataAgain()
    {
        var display = new DisplayConfig
        {
            Icons = "unicode",
            TypeColors = new() { ["Nebula"] = "7BD8EA" },
        };
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Nebula", IconId = "icon_diamond", Color = "DA2244" },
        };
        var states = new[] { new StateEntry("Teleporting", StateCategory.Removed, null) };
        var item = Item("A custom process item");
        var observation = Observation(item, new SpectreTheme(display, appearances, states));
        var before = string.Join("\n", BrowserBenchPresentation.Rows(observation, item, "", 100, false, false).Select(r => r.Text));
        Plain(before).ShouldContain("◇ Nebula");
        before.ShouldContain("38;2;123;216;234");
        before.ShouldNotContain("38;2;218;34;68");
        var nativeAppearance = Observation(item, new SpectreTheme(new DisplayConfig { Icons = "unicode" }, appearances, states));
        string.Join("\n", BrowserBenchPresentation.Rows(nativeAppearance, item, "", 100, false, false).Select(r => r.Text))
            .ShouldContain("38;2;218;34;68");
        var unclassified = Observation(item, new SpectreTheme(display, appearances));
        string.Join("\n", BrowserBenchPresentation.Rows(unclassified, item, "", 100, false, false).Select(r => r.Text))
            .ShouldNotBe(before);

        states[0] = new("Teleporting", StateCategory.Completed, null);
        display.TypeColors["Nebula"] = "000000";
        appearances[0].IconId = "icon_crown";
        string.Join("\n", BrowserBenchPresentation.Rows(observation, item, "", 100, false, false).Select(r => r.Text))
            .ShouldBe(before);
    }

    [Fact]
    public void WrappedTreeRowsKeepUnicodeAndOffsetsWithReadableFullWidthSelection()
    {
        const string title = "Aperture界e\u0301👩‍💻Nebula界e\u0301👩‍💻Terminal";
        const string prefix = "│ └▾ ";
        var item = Item(title) with { Owed = true, Seed = true, SinglePin = true, TreePin = true, SubtreeOwners = [42, 7] };
        var theme = new SpectreTheme(new DisplayConfig { Icons = "unicode", TypeColors = new() { ["Nebula"] = "16324F" } },
            [new() { Name = "Nebula", IconId = "icon_diamond" }], [new("Teleporting", StateCategory.Removed, null)]);
        var rows = BrowserBenchPresentation.Rows(Observation(item, theme), item, prefix, 32, true, false);
        var logical = "#42 ◇ Nebula ● Teleporting  " + title + "  ● seed  ● owed  ◆ single pin  ↳ subtree pin  ↳ inherited #7";
        for (var i = 0; i < rows.Count; i++)
        {
            var plain = Plain(rows[i].Text);
            BrowserTerminalText.CellWidth(plain).ShouldBe(32);
            plain.ShouldStartWith(i == 0 ? prefix : new string(' ', prefix.Length));
            var end = i + 1 < rows.Count ? rows[i + 1].Offset : logical.Length;
            plain[prefix.Length..].ShouldStartWith(logical[rows[i].Offset..end]);
            InkAt(rows[i].Text, rows[i].Text.LastIndexOf(' ')).Background.ShouldBe(new Color(22, 50, 79));
        }
        var badge = rows.First(r => Plain(r.Text).Contains('◇')).Text;
        var state = rows.First(r => Plain(r.Text).Contains("● Teleporting", StringComparison.Ordinal)).Text;
        var typeInk = InkAt(badge, badge.IndexOf('◇'));
        var stateInk = InkAt(state, state.IndexOf('●'));
        Contrast(typeInk.Foreground, typeInk.Background).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(stateInk.Foreground, stateInk.Background).ShouldBeGreaterThanOrEqualTo(4.5);
        typeInk.Foreground.ShouldNotBe(stateInk.Foreground);
    }

    [Fact]
    public void TableWrapsLongCustomMetadataAndTitleRatherThanTruncatingThem()
    {
        const string title = "界Aperture界Aperture界Aperture界Aperture界Aperture";
        var item = Item(title) with { Type = "NebulaObservatoryTelemetry", State = "TeleportingAcrossDimensions" };
        var observation = Observation(item, new SpectreTheme(new DisplayConfig { Icons = "unicode" }));
        var rows = BrowserBenchPresentation.Rows(observation, item, "", 60, false, true);
        var plain = rows.Select(r => Plain(r.Text)).ToArray();
        foreach (var line in plain) BrowserTerminalText.CellWidth(line).ShouldBe(60);
        string.Concat(plain.Select(line => line.Substring(13, 12).TrimEnd())).ShouldContain(item.Type);
        string.Concat(plain.Select(line => line.Substring(26, 12).TrimEnd())).ShouldContain(item.State);
        string.Concat(plain.Select(line => line[39..].TrimEnd())).ShouldBe(title);
        var headingLength = $"#42 N {item.Type} ● {item.State}  ".Length;
        for (var row = 1; row < rows.Count; row++)
            title[(rows[row].Offset - headingLength)..].ShouldStartWith(plain[row][39..].TrimEnd());
        var narrow = BrowserBenchPresentation.Rows(observation, item, "", 24, false, true);
        string.Concat(narrow.Select(r => Plain(r.Text).TrimEnd())).ShouldContain(title);
    }

    [Fact]
    public async Task FullItemAndSelectedFieldKeepReadableSurfacesAcrossMixedTextStyles()
    {
        var item = NativeBrowserFixture.Item(42, "Focus field");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p><strong>Bold</strong> plain <em>Emphasis</em></p>",
        });
        var snapshot = new BrowserDetailSnapshot(item, null, [], [], null, [], [], null, 15,
            new SpectreTheme(new DisplayConfig()));
        var rows = await snapshot.RenderRowsAsync(80);
        var description = rows.Single(row => row.Source == "System.Description" && Plain(row.Text).Contains("Bold", StringComparison.Ordinal)).Text;
        foreach (var word in new[] { "Bold", "plain", "Emphasis" })
            InkAt(description, description.IndexOf(word, StringComparison.Ordinal)).Background.ShouldBe(BrowserTerminalFrame.ContentBackground);
        var editor = await snapshot.RenderEditingRowsAsync(80, new Twig.Domain.Services.WorkItemMapper().ToSnapshot(item),
            new Dictionary<string, string?>(), "System.Title");
        var title = editor.Single(row => row.Source == "System.Title").Text;
        var selected = InkAt(title, title.IndexOf("Focus", StringComparison.Ordinal));
        selected.Background.ShouldBe(BrowserTerminalFrame.SelectionBackground);
        Contrast(selected.Foreground, selected.Background).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    private static BrowserBenchItem Item(string title) => new(42, null, "Nebula", "Teleporting", title,
        true, true, false, false, false, false, []);

    private static BrowserBenchObservation Observation(BrowserBenchItem item, SpectreTheme theme)
    {
        var origin = new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
            new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
            "fixture", "fixture", 1,
            new ConnectionOperationSnapshot("org", "project", "team", "fingerprint", 1, "manifest", "config", "unicode", "config"));
        return new(new("bench", "bench", origin, "contents"), "settings", [item], [], [], [], "", theme.Snapshot(), "");
    }

    private static string Plain(string text) => Sgr.Replace(text, "");

    private static (Color Foreground, Color Background) InkAt(string ansi, int position)
    {
        var foreground = Color.Default;
        var background = Color.Default;
        foreach (Match match in Sgr.Matches(ansi))
        {
            if (match.Index >= position) break;
            var codes = match.Value[2..^1].Split(';').Select(value => value.Length == 0 ? 0 : int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            for (var i = 0; i < codes.Length; i++)
            {
                if (codes[i] == 0) { foreground = Color.Default; background = Color.Default; }
                if (codes[i] == 39) foreground = Color.Default;
                if (codes[i] == 49) background = Color.Default;
                if (codes[i] is not (38 or 48) || i + 4 >= codes.Length || codes[i + 1] != 2) continue;
                var color = new Color((byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4]);
                if (codes[i] == 38) foreground = color; else background = color;
                i += 4;
            }
        }
        return (foreground, background);
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
