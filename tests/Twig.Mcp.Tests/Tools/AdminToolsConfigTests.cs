using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="AdminTools.Config"/> (twig_config MCP tool).
/// Covers full config retrieval, individual key lookups, unknown keys,
/// workspace resolution, and config values with defaults vs custom settings.
/// </summary>
public sealed class AdminToolsConfigTests : ReadToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Full config — no key parameter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_NoKey_ReturnsFullConfiguration()
    {
        var sut = CreateAdminSut(DefaultConfig);
        var result = await sut.Config();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.TryGetProperty("config", out var configElement).ShouldBeTrue();
        configElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Config_NoKey_ConfigContainsExpectedSections()
    {
        var config = new TwigConfiguration
        {
            Organization = "myorg",
            Project = "myproject",
            Team = "myteam",
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        var configEl = data.GetProperty("config");

        configEl.GetProperty("organization").GetString().ShouldBe("myorg");
        configEl.GetProperty("project").GetString().ShouldBe("myproject");
        configEl.GetProperty("team").GetString().ShouldBe("myteam");
    }

    [Fact]
    public async Task Config_NoKey_IncludesNestedSections()
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { Hints = false, TreeDepth = 10 },
            Seed = new SeedConfig { StaleDays = 30 },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        var configEl = data.GetProperty("config");

        var display = configEl.GetProperty("display");
        display.GetProperty("hints").GetBoolean().ShouldBeFalse();
        display.GetProperty("treeDepth").GetInt32().ShouldBe(10);

        var seed = configEl.GetProperty("seed");
        seed.GetProperty("staleDays").GetInt32().ShouldBe(30);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single key lookup — known keys
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_WithKey_ReturnsKeyValuePair()
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { Hints = false },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "display.hints");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("key").GetString().ShouldBe("display.hints");
        data.GetProperty("value").GetString().ShouldBe("false");
    }

    [Theory]
    [InlineData("organization", "testorg")]
    [InlineData("project", "testproj")]
    [InlineData("team", "myteam")]
    [InlineData("auth.method", "azcli")]
    [InlineData("defaults.mode", "sprint")]
    [InlineData("seed.staledays", "14")]
    [InlineData("display.icons", "unicode")]
    [InlineData("tracking.cleanuppolicy", "none")]
    [InlineData("areas.mode", "under")]
    public async Task Config_WithKey_ReturnsExpectedValues(string key, string expectedValue)
    {
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Team = "myteam",
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: key);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("key").GetString().ShouldBe(key);
        data.GetProperty("value").GetString().ShouldBe(expectedValue);
    }

    [Fact]
    public async Task Config_WithKey_UserName_ReturnsConfiguredValue()
    {
        var config = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Jane Doe" },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "user.name");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("Jane Doe");
    }

    [Fact]
    public async Task Config_WithKey_NullableProperty_ReturnsEmptyString()
    {
        var config = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = null },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "user.name");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single key lookup — unknown key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_UnknownKey_ReturnsError()
    {
        var sut = CreateAdminSut(DefaultConfig);
        var result = await sut.Config(key: "nonexistent.key");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Unknown configuration key");
        GetErrorText(result).ShouldContain("nonexistent.key");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Key lookup — case insensitivity
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Display.Hints")]
    [InlineData("DISPLAY.HINTS")]
    [InlineData("display.HINTS")]
    public async Task Config_KeyIsCaseInsensitive(string key)
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { Hints = true },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: key);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("true");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution — invalid workspace
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_InvalidWorkspace_ReturnsError()
    {
        var sut = CreateAdminSut(DefaultConfig);
        var result = await sut.Config(workspace: "invalid/workspace");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Envelope shape — success envelope has context block
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_SuccessEnvelope_HasContextBlock()
    {
        var sut = CreateAdminSut(DefaultConfig);
        var result = await sut.Config();

        var envelope = ParseEnvelope(result);
        envelope.GetProperty("success").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("data", out _).ShouldBeTrue();
        envelope.TryGetProperty("context", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Config_WithKey_SuccessEnvelope_HasContextBlock()
    {
        var sut = CreateAdminSut(DefaultConfig);
        var result = await sut.Config(key: "display.hints");

        var envelope = ParseEnvelope(result);
        envelope.GetProperty("success").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("data", out _).ShouldBeTrue();
        envelope.TryGetProperty("context", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  List-valued keys
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_WithKey_ListProperty_ReturnsSemicolonJoined()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPaths = ["Project\\Area1", "Project\\Area2"],
            },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "defaults.areapaths");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("Project\\Area1;Project\\Area2");
    }

    [Fact]
    public async Task Config_WithKey_EmptyList_ReturnsEmptyString()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig { AreaPaths = [] },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "defaults.areapaths");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Numeric and boolean keys
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_WithKey_NumericProperty_ReturnsStringifiedNumber()
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepthUp = 5 },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "display.treedepthup");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("5");
    }

    [Fact]
    public async Task Config_WithKey_DoubleProperty_ReturnsInvariantFormatted()
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { FillRateThreshold = 0.75 },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "display.fillratethreshold");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("0.75");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace sprints key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_WithKey_WorkspaceSprints_ReturnsSemicolonJoinedExpressions()
    {
        var config = new TwigConfiguration
        {
            Workspace = new WorkspaceConfig
            {
                Sprints = [new SprintEntry { Expression = "@current" }, new SprintEntry { Expression = "@current-1" }],
            },
        };

        var sut = CreateAdminSut(config);
        var result = await sut.Config(key: "workspace.sprints");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("value").GetString().ShouldBe("@current;@current-1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private AdminTools CreateAdminSut(TwigConfiguration config)
    {
        var resolver = BuildResolver(config);
        return new AdminTools(resolver);
    }
}
