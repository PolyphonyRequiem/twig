using Shouldly;
using Twig.Browser;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserTerminalInputTests
{
    [Fact]
    public void BracketedPastePreservesWhitespaceAndCannotBecomeShortcuts()
    {
        var parser = new BrowserVtInputParser();
        foreach (var character in "\x1b[200~q  a\n\x03\x1b[201~") parser.Feed(character);
        var output = Drain(parser);
        new string(output.Select(input => input.Character).ToArray()).ShouldBe("q  a\n");
        output.All(input => input.Kind == BrowserInputKind.Key && input.Key == BrowserKey.Character && input.Modifiers == BrowserModifiers.Paste).ShouldBeTrue();
        parser.Feed('q');
        parser.TryRead(out var next).ShouldBeTrue();
        next.Modifiers.ShouldBe(BrowserModifiers.None);
    }

    [Fact]
    public void FragmentedMouseCoordinatesAndModifiersAreNormalized()
    {
        var parser = new BrowserVtInputParser();
        foreach (var character in "\x1b[<84;12;") parser.Feed(character);
        parser.TryRead(out _).ShouldBeFalse();
        foreach (var character in "7M") parser.Feed(character);
        parser.TryRead(out var mouse).ShouldBeTrue();
        mouse.Kind.ShouldBe(BrowserInputKind.Mouse);
        mouse.X.ShouldBe(11); mouse.Y.ShouldBe(6); mouse.WheelDelta.ShouldBe(1);
        mouse.Modifiers.ShouldBe(BrowserModifiers.Control | BrowserModifiers.Shift);
        mouse.ButtonDown.ShouldBeFalse();
    }

    [Fact]
    public void HeldButtonMotionIsNotAnotherClick()
    {
        var parser = new BrowserVtInputParser();
        foreach (var character in "\x1b[<0;5;3M\x1b[<32;6;3M\x1b[<0;6;3m") parser.Feed(character);
        var output = Drain(parser);
        output[0].ButtonDown.ShouldBeTrue();
        output[1].Kind.ShouldBe(BrowserInputKind.Mouse);
        output[1].X.ShouldBe(5);
        output[1].ButtonDown.ShouldBeFalse();
        output[2].ButtonDown.ShouldBeFalse();
    }


    [Fact]
    public void ModifiedArrowAndControlCharacterAreKeyboardEvents()
    {
        var parser = new BrowserVtInputParser();
        foreach (var character in "\x1b[1;6D\x03") parser.Feed(character);
        var output = Drain(parser);
        output[0].Key.ShouldBe(BrowserKey.Left);
        output[0].Modifiers.ShouldBe(BrowserModifiers.Control | BrowserModifiers.Shift);
        output[1].Character.ShouldBe('c'); output[1].Modifiers.ShouldBe(BrowserModifiers.Control);
    }

    [Fact]
    public void EndOfInputNeverExecutesAnIncompleteEscape()
    {
        var parser = new BrowserVtInputParser();
        foreach (var character in "\x1b[200~q\x1b[") parser.Feed(character);
        parser.EndInput();
        var output = Drain(parser);
        output[0].Character.ShouldBe('q'); output[0].Modifiers.ShouldBe(BrowserModifiers.Paste);
        output[1].Kind.ShouldBe(BrowserInputKind.EndOfInput);
        output.ShouldNotContain(input => input.Key == BrowserKey.Escape);
    }

    [Fact]
    public void FooterOccupiesTheOnlyVisibleRowAndClipsHorizontally()
    {
        using var writer = new StringWriter();
        BrowserTerminalFrame.Paint(writer, ["content"], 0, 4, 1, new("header", "context", "notice", "status", null));
        writer.ToString().ShouldContain("\x1b[1;1H");
        writer.ToString().ShouldContain("stat");
        writer.ToString().ShouldNotContain("header");
        writer.ToString().ShouldNotContain("content");
        writer.ToString().ShouldNotContain("\x1b[2;1H");
    }

    [Fact]
    public void HostContextFitsTheHeaderWithoutDisplacingTheFooter()
    {
        var host = new BrowserHostContext("Herdr · a long workspace and tab label", "Herdr · p1", "captured host");
        using var writer = new StringWriter();
        BrowserTerminalFrame.Paint(writer, [], 0, 40, 2, new("Connection · Bench · User", "", "", "[Esc Back]", host));
        var painted = writer.ToString();
        var header = BrowserTerminalText.PlainText(painted[..painted.IndexOf("\x1b[2;1H", StringComparison.Ordinal)]);
        BrowserTerminalText.CellWidth(header).ShouldBe(40);
        header.ShouldStartWith("Connection · Bench");
        header.ShouldEndWith("Herdr · p1");
        BrowserTerminalText.PlainText(painted).ShouldContain("[Esc Back]");

        writer.GetStringBuilder().Clear();
        BrowserTerminalFrame.Paint(writer, [], 0, 1, 1, new("Header", "", "", "Quit", host));
        BrowserTerminalText.PlainText(writer.ToString()).ShouldBe("Q");
    }

    private static List<BrowserInput> Drain(BrowserVtInputParser parser)
    {
        var inputs = new List<BrowserInput>();
        while (parser.TryRead(out var input)) inputs.Add(input);
        return inputs;
    }
}
