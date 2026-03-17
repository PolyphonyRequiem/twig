using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class OhMyPoshCommandsTests
{
    // ── (a) Powerline style output contains valid JSON with "type": "text" ──

    [Fact]
    public void PowerlineStyle_ContainsValidJsonWithTextType()
    {
        var json = OhMyPoshCommands.GenerateSegmentJson("powerline");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().ShouldBe("text");
        root.GetProperty("style").GetString().ShouldBe("powerline");
        root.GetProperty("powerline_symbol").GetString().ShouldBe("\uE0B0");
        root.GetProperty("foreground").GetString().ShouldBe("#ffffff");
        root.GetProperty("background").GetString().ShouldBe("#0078D4");
    }

    // ── (b) Plain style output ──────────────────────────────────────────────

    [Fact]
    public void PlainStyle_ContainsValidJsonWithTextType()
    {
        var json = OhMyPoshCommands.GenerateSegmentJson("plain");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().ShouldBe("text");
        root.GetProperty("style").GetString().ShouldBe("plain");
        root.GetProperty("foreground").GetString().ShouldBe("#0078D4");
        root.TryGetProperty("background", out _).ShouldBeFalse();
        root.TryGetProperty("powerline_symbol", out _).ShouldBeFalse();
    }

    // ── (c) Diamond style output ────────────────────────────────────────────

    [Fact]
    public void DiamondStyle_ContainsValidJsonWithTextType()
    {
        var json = OhMyPoshCommands.GenerateSegmentJson("diamond");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().ShouldBe("text");
        root.GetProperty("style").GetString().ShouldBe("diamond");
        root.GetProperty("leading_diamond").GetString().ShouldBe("\uE0B6");
        root.GetProperty("trailing_diamond").GetString().ShouldBe("\uE0B4");
        root.GetProperty("foreground").GetString().ShouldBe("#ffffff");
        root.GetProperty("background").GetString().ShouldBe("#0078D4");
    }

    // ── (d) PowerShell hook contains Set-PoshContext ────────────────────────

    [Fact]
    public void PowerShellHook_ReadsPromptJson()
    {
        var hook = OhMyPoshCommands.GenerateShellHook("pwsh");

        hook.ShouldContain("Set-PoshContext");
        hook.ShouldContain("Set-TwigPrompt");
        hook.ShouldContain("prompt.json");
        hook.ShouldContain("ConvertFrom-Json");
        hook.ShouldContain("TWIG_TYPE_COLOR");
        hook.ShouldContain("TWIG_STATE_CATEGORY");
        hook.ShouldNotContain("twig _prompt");
    }

    // ── (e) Bash hook reads prompt.json ─────────────────────────────────────

    [Fact]
    public void BashHook_ReadsPromptJson()
    {
        var hook = OhMyPoshCommands.GenerateShellHook("bash");

        hook.ShouldContain("set_poshcontext()");
        hook.ShouldContain("prompt.json");
        hook.ShouldContain("jq");
        hook.ShouldContain("export TWIG_PROMPT");
        hook.ShouldContain("TWIG_TYPE_COLOR");
        hook.ShouldContain("TWIG_STATE_CATEGORY");
        hook.ShouldNotContain("twig _prompt");
    }

    // ── Zsh hook reads prompt.json ──────────────────────────────────────────

    [Fact]
    public void ZshHook_ReadsPromptJson()
    {
        var hook = OhMyPoshCommands.GenerateShellHook("zsh");

        hook.ShouldContain("set_poshcontext()");
        hook.ShouldContain("prompt.json");
        hook.ShouldContain("jq");
        hook.ShouldContain("export TWIG_PROMPT");
        hook.ShouldContain("TWIG_TYPE_COLOR");
        hook.ShouldContain("TWIG_STATE_CATEGORY");
        hook.ShouldNotContain("twig _prompt");
    }

    // ── (f) Fish hook reads prompt.json ─────────────────────────────────────

    [Fact]
    public void FishHook_ReadsPromptJson()
    {
        var hook = OhMyPoshCommands.GenerateShellHook("fish");

        hook.ShouldContain("set -gx TWIG_PROMPT");
        hook.ShouldContain("set_poshcontext");
        hook.ShouldContain("prompt.json");
        hook.ShouldContain("jq");
        hook.ShouldContain("TWIG_TYPE_COLOR");
        hook.ShouldContain("TWIG_STATE_CATEGORY");
        hook.ShouldNotContain("twig _prompt");
    }

    // ── (g) Template contains {{ .Env.TWIG_PROMPT }} ────────────────────────

    [Theory]
    [InlineData("powerline")]
    [InlineData("plain")]
    [InlineData("diamond")]
    public void Template_ContainsTwigPromptEnvVar(string style)
    {
        var json = OhMyPoshCommands.GenerateSegmentJson(style);
        var doc = JsonDocument.Parse(json);
        var template = doc.RootElement.GetProperty("template").GetString();
        template.ShouldBe("{{ if .Env.TWIG_PROMPT }} {{ .Env.TWIG_PROMPT }} {{ end }}");
    }

    // ── (h) No "type": "command" anywhere in output ─────────────────────────

    [Theory]
    [InlineData("powerline")]
    [InlineData("plain")]
    [InlineData("diamond")]
    public void NoCommandTypeInOutput(string style)
    {
        var json = OhMyPoshCommands.GenerateSegmentJson(style);
        json.ShouldNotContain("\"type\": \"command\"");
        json.ShouldNotContain("\"type\":\"command\"");
    }

    // ── Cache section present ───────────────────────────────────────────────

    [Theory]
    [InlineData("powerline")]
    [InlineData("plain")]
    [InlineData("diamond")]
    public void AllStyles_ContainsCacheSection(string style)
    {
        var json = OhMyPoshCommands.GenerateSegmentJson(style);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cache = root.GetProperty("cache");
        cache.GetProperty("duration").GetString().ShouldBe("30s");
        cache.GetProperty("strategy").GetString().ShouldBe("folder");
    }

    // ── Default style is powerline ──────────────────────────────────────────

    [Fact]
    public void DefaultStyle_IsPowerline()
    {
        var json = OhMyPoshCommands.GenerateSegmentJson("unknown-style");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("style").GetString().ShouldBe("powerline");
    }

    // ── Default shell is PowerShell ─────────────────────────────────────────

    [Fact]
    public void DefaultShell_IsPowerShell()
    {
        var hook = OhMyPoshCommands.GenerateShellHook("unknown-shell");
        hook.ShouldContain("Set-PoshContext");
    }

    // ── All JSON outputs are valid and parseable ────────────────────────────

    [Theory]
    [InlineData("powerline")]
    [InlineData("plain")]
    [InlineData("diamond")]
    public void AllStyles_ProduceValidJson(string style)
    {
        var json = OhMyPoshCommands.GenerateSegmentJson(style);
        Should.NotThrow(() => JsonDocument.Parse(json));
    }
}
