using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_MatchesRegisteredToolsAndBatchSupportsEveryNonBatchTool()
    {
        var tools = GetRegisteredTools();
        var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        names.SetEquals(McpToolCatalog.AllToolNames).ShouldBeTrue();
        McpToolCatalog.BatchableToolNames.SetEquals(
            names.Where(name => name != "twig_batch")).ShouldBeTrue();
    }

    [Fact]
    public void CompactProfile_ExposesTenAnnotatedToolsWithinBudget()
    {
        var result = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = GetRegisteredTools() },
            McpToolProfile.Compact,
            exposeWorkspaceOverride: false);

        result.Tools.Count.ShouldBe(10);
        result.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(McpToolCatalog.CompactToolNames).ShouldBeTrue();
        GetSerializedSize(result.Tools).ShouldBeLessThanOrEqualTo(8_500);

        foreach (var tool in result.Tools)
        {
            JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions)
                .ShouldContain("\"execution\"");
            tool.Annotations.ShouldNotBeNull();
            tool.Annotations.ReadOnlyHint.ShouldNotBeNull();
            tool.Annotations.DestructiveHint.ShouldNotBeNull();
            tool.Annotations.IdempotentHint.ShouldNotBeNull();
            tool.Annotations.OpenWorldHint.ShouldNotBeNull();

            var properties = tool.InputSchema.GetProperty("properties");
            properties.TryGetProperty("verbose", out _).ShouldBeFalse();
            properties.TryGetProperty("workspace", out _).ShouldBeFalse();
            tool.InputSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public void FullProfile_PreservesAllToolsButRemovesUniversalVerbose()
    {
        var result = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = GetRegisteredTools() },
            McpToolProfile.Full,
            exposeWorkspaceOverride: true);

        result.Tools.Count.ShouldBe(40);
        GetSerializedSize(result.Tools).ShouldBeLessThanOrEqualTo(37_000);

        var workspaceCount = 0;
        foreach (var tool in result.Tools)
        {
            var properties = tool.InputSchema.GetProperty("properties");
            properties.TryGetProperty("verbose", out _).ShouldBeFalse();
            if (properties.TryGetProperty("workspace", out _)) workspaceCount++;
        }

        workspaceCount.ShouldBe(39);
    }

    [Fact]
    public void FullProfile_UsesTypedSchemasForStructuredArguments()
    {
        var tools = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = GetRegisteredTools() },
            McpToolProfile.Full,
            exposeWorkspaceOverride: true).Tools.ToDictionary(tool => tool.Name);

        tools["twig_batch"].InputSchema.GetProperty("properties").GetProperty("graph")
            .GetProperty("type").GetString().ShouldBe("object");
        tools["twig_patch"].InputSchema.GetProperty("properties").GetProperty("fields")
            .GetProperty("type").GetString().ShouldBe("object");
        tools["twig_track"].InputSchema.GetProperty("properties").GetProperty("id")
            .TryGetProperty("oneOf", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("twig_batch", false, true, false, true)]
    [InlineData("twig_set", false, false, false, true)]
    [InlineData("twig_refresh", false, false, false, true)]
    [InlineData("twig_seed_publish", false, true, false, true)]
    [InlineData("twig_cache_status", true, false, true, false)]
    public void FullProfile_UsesConservativeSafetyAnnotations(
        string name,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        var tool = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = GetRegisteredTools() },
            McpToolProfile.Full,
            exposeWorkspaceOverride: true).Tools.Single(tool => tool.Name == name);

        tool.Annotations.ShouldNotBeNull();
        tool.Annotations.ReadOnlyHint.ShouldBe(readOnly);
        tool.Annotations.DestructiveHint.ShouldBe(destructive);
        tool.Annotations.IdempotentHint.ShouldBe(idempotent);
        tool.Annotations.OpenWorldHint.ShouldBe(openWorld);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("compact", false)]
    [InlineData("core", false)]
    [InlineData("full", true)]
    [InlineData("all", true)]
    public void ResolveProfile_ParsesEnvironmentValue(string? value, bool isFull)
    {
        McpToolCatalog.ResolveProfile([], value).ShouldBe(
            isFull ? McpToolProfile.Full : McpToolProfile.Compact);
    }

    [Fact]
    public void ResolveProfile_CommandLineOverridesEnvironment()
    {
        McpToolCatalog.ResolveProfile(["--tool-profile", "full"], "compact")
            .ShouldBe(McpToolProfile.Full);
        McpToolCatalog.ResolveProfile(["--tool-profile=compact"], "full")
            .ShouldBe(McpToolProfile.Compact);
    }

    [Fact]
    public void ResolveProfile_RejectsUnknownValue()
    {
        var error = Should.Throw<ArgumentException>(() =>
            McpToolCatalog.ResolveProfile([], "huge"));

        error.Message.ShouldContain("Valid profiles: compact, full");
    }

    [Fact]
    public void FilterList_PreservesCursorAndDoesNotMutateRegisteredSchema()
    {
        var registered = GetRegisteredTools();
        var sourceTool = registered.Single(tool => tool.Name == "twig_set");
        var sourceSchema = sourceTool.InputSchema.GetRawText();

        var result = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = registered, NextCursor = "next" },
            McpToolProfile.Compact,
            exposeWorkspaceOverride: false);

        result.NextCursor.ShouldBe("next");
        sourceTool.InputSchema.GetRawText().ShouldBe(sourceSchema);
        sourceTool.InputSchema.GetProperty("properties")
            .TryGetProperty("workspace", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("twig_batch", "graph", "{\"type\":\"sequence\",\"steps\":[]}")]
    [InlineData("twig_patch", "fields", "{\"System.Title\":\"New\"}")]
    [InlineData("twig_track", "id", "[1,2,3]")]
    public void RewriteStructuredArguments_AdaptsTypedJsonToLegacyStrings(
        string toolName,
        string argumentName,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var request = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = new Dictionary<string, JsonElement>
            {
                [argumentName] = document.RootElement.Clone(),
            },
        };

        McpToolCatalog.RewriteStructuredArguments(request);

        var rewritten = request.Arguments[argumentName];
        rewritten.ValueKind.ShouldBe(JsonValueKind.String);
        rewritten.GetString().ShouldBe(json);
    }

    private static List<Tool> GetRegisteredTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddMcpServer()
            .WithTools<ContextTools>()
            .WithTools<ReadTools>()
            .WithTools<MutationTools>()
            .WithTools<NavigationTools>()
            .WithTools<CreationTools>()
            .WithTools<WorkspaceTools>()
            .WithTools<ProcessTools>()
            .WithTools<AdminTools>()
            .WithTools<TrackingTools>()
            .WithTools<BatchTools>()
            .WithTools<SeedTools>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.ToolCollection.ShouldNotBeNull();
        return options.ToolCollection.ToArray().Select(tool => tool.ProtocolTool).ToList();
    }

    private static int GetSerializedSize(IEnumerable<Tool> tools)
    {
        var size = 2; // []
        var count = 0;
        foreach (var tool in tools)
        {
            if (count++ > 0) size++; // comma
            size += JsonSerializer.SerializeToUtf8Bytes(
                tool,
                McpJsonUtilities.DefaultOptions).Length;
        }

        return size;
    }
}
