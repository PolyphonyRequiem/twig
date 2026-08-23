using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

/// <summary>
/// Focused catalog/metadata tests for the plan lifecycle tools. These bind to the same
/// registered tool metadata a client sees, so a change to
/// <see cref="McpToolCatalog.AllToolNames"/>, the annotation sets or the typed schema
/// application must be intentional to keep the assertions green.
/// </summary>
public sealed class PlanCatalogTests
{
    [Theory]
    [InlineData("twig_plan_validate")]
    [InlineData("twig_plan_preview")]
    [InlineData("twig_plan_apply")]
    [InlineData("twig_plan_status")]
    [InlineData("twig_plan_seed")]
    [InlineData("twig_pending")]
    public void PlanTools_AreCataloged(string name)
    {
        McpToolCatalog.AllToolNames.ShouldContain(name);
        // Every plan tool is composable inside twig_batch.
        McpToolCatalog.BatchableToolNames.ShouldContain(name);
    }

    /// <summary>
    /// The safety annotations tell an agent how much of a commitment each call is. Both
    /// preview and apply mutate durable state; only preview is safely re-runnable per digest.
    /// </summary>
    [Theory]
    [InlineData("twig_plan_validate", true, false, true, false)]  // read-only, no ADO
    [InlineData("twig_plan_preview", false, false, true, false)]  // writes journal, idempotent per digest, no ADO
    [InlineData("twig_plan_apply", false, true, false, true)]     // ADO mutation
    [InlineData("twig_plan_status", true, false, true, false)]    // read-only, no ADO
    [InlineData("twig_plan_seed", true, false, true, false)]      // read-only, no ADO
    [InlineData("twig_pending", true, false, true, false)]        // read-only, no ADO
    public void PlanTools_UseCorrectAnnotations(
        string name,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        var tool = GetTool(name);
        tool.Annotations.ShouldNotBeNull();
        tool.Annotations.ReadOnlyHint.ShouldBe(readOnly);
        tool.Annotations.DestructiveHint.ShouldBe(destructive);
        tool.Annotations.IdempotentHint.ShouldBe(idempotent);
        tool.Annotations.OpenWorldHint.ShouldBe(openWorld);
    }

    [Fact]
    public void PlanApply_Schema_PinsConfirmedTrueAndDigestPattern()
    {
        var properties = GetProperties("twig_plan_apply");

        var confirmed = properties.GetProperty("confirmed");
        confirmed.GetProperty("type").GetString().ShouldBe("boolean");
        var enumValues = confirmed.GetProperty("enum");
        enumValues.GetArrayLength().ShouldBe(1);
        enumValues[0].GetBoolean().ShouldBeTrue();

        var digest = properties.GetProperty("confirmedDigest");
        digest.GetProperty("type").GetString().ShouldBe("string");
        digest.GetProperty("pattern").GetString().ShouldBe(PlanTools.DigestPattern);
        digest.GetProperty("minLength").GetInt32().ShouldBe(64);
        digest.GetProperty("maxLength").GetInt32().ShouldBe(64);
    }

    [Fact]
    public void PlanApply_Schema_RequiresFileConfirmedAndDigest()
    {
        var tool = GetTool("twig_plan_apply");
        var required = tool.InputSchema.GetProperty("required");
        var names = new List<string>();
        for (var i = 0; i < required.GetArrayLength(); i++) names.Add(required[i].GetString()!);

        names.ShouldContain("file");
        names.ShouldContain("confirmed");
        names.ShouldContain("confirmedDigest");
    }

    [Fact]
    public void PlanSeed_Schema_ForbidsNonNegativeIds()
    {
        var properties = GetProperties("twig_plan_seed");
        properties.GetProperty("id").GetProperty("maximum").GetInt32().ShouldBe(-1);
    }

    [Theory]
    [InlineData("twig_plan_validate")]
    [InlineData("twig_plan_preview")]
    [InlineData("twig_plan_status")]
    [InlineData("twig_plan_apply")]
    public void PlanFileTools_RequireNonEmptyFile(string name)
    {
        var properties = GetProperties(name);
        properties.GetProperty("file").GetProperty("minLength").GetInt32().ShouldBe(1);
    }

    private static JsonElement GetProperties(string toolName) =>
        GetTool(toolName).InputSchema.GetProperty("properties");

    private static Tool GetTool(string toolName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddMcpServer()
            .WithTools<ReadTools>()
            .WithTools<MutationTools>()
            .WithTools<NavigationTools>()
            .WithTools<CreationTools>()
            .WithTools<WorkspaceTools>()
            .WithTools<ProcessTools>()
            .WithTools<AdminTools>()
            .WithTools<TrackingTools>()
            .WithTools<BatchTools>()
            .WithTools<SeedTools>()
            .WithTools<PlanTools>();

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>()
            .Value;
        var tools = options.ToolCollection!.ToArray().Select(t => t.ProtocolTool).ToList();

        var filtered = McpToolCatalog.FilterList(
            new ListToolsResult { Tools = tools },
            McpToolProfile.Full,
            exposeWorkspaceOverride: true);
        return filtered.Tools.Single(t => t.Name == toolName);
    }
}
