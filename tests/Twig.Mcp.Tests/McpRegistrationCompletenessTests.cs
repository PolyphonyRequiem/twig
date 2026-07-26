using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

/// <summary>
/// Wayfinder ticket 0008 — MCP registration completeness.
///
/// An MCP tool class only works when THREE things line up, and
/// <c>Twig.Mcp/Program.cs</c> comments the trap on itself:
///
///   1. <c>builder.Services.AddSingleton&lt;XTools&gt;()</c> — the service container
///   2. <c>.WithTools&lt;XTools&gt;()</c> — MCP discovery metadata, which does NOT
///      add the type to the container
///   3. every <c>[McpServerTool(Name = ...)]</c> present in
///      <see cref="McpToolCatalog.AllToolNames"/>, or the tool is invisible in every
///      profile and un-batchable
///
/// Miss (1) and discovery advertises a tool that throws on call. Miss (2) and a
/// registered class is never advertised. Miss (3) and the tool exists but no client
/// can see it. All three failures are silent — that is #269/#270/#279.
/// </summary>
public sealed class McpRegistrationCompletenessTests
{
    private static readonly Regex AddSingletonPattern =
        new(@"AddSingleton<(?<type>\w+Tools)>\(\)", RegexOptions.Compiled);

    private static readonly Regex WithToolsPattern =
        new(@"\.WithTools<(?<type>\w+Tools)>\(\)", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>[McpServerToolType]</c> class in the assembly — the ground truth
    /// that both registration lists are checked against.
    /// </summary>
    private static IReadOnlyList<Type> DiscoverToolTypes()
    {
        var types = typeof(AdminTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        types.Count.ShouldBeGreaterThan(5,
            "Tool-type discovery found almost nothing — [McpServerToolType] discovery broke "
            + "and this guard has gone vacuous. Fix the discovery, do not delete the assertion.");

        return types;
    }

    private static string ReadMcpProgramSource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Twig.slnx")))
            dir = Path.GetDirectoryName(dir);

        dir.ShouldNotBeNull("Could not find repository root (looked for Twig.slnx)");

        var path = Path.Combine(dir, "src", "Twig.Mcp", "Program.cs");
        File.Exists(path).ShouldBeTrue(
            $"Expected MCP bootstrap source at {path}. If it moved, update this test — "
            + "do not delete it; it is the only guard on MCP tool wiring.");

        // Strip line comments before scraping. A commented-out registration is a
        // DISABLED registration; counting it would let the exact regression this
        // test guards against slip through wearing a "//".
        var lines = File.ReadAllLines(path)
            .Select(line =>
            {
                var index = line.IndexOf("//", StringComparison.Ordinal);
                return index >= 0 ? line[..index] : line;
            });

        return string.Join('\n', lines);
    }

    /// <summary>Touch point 1: container registration.</summary>
    [Fact]
    public void EveryToolType_IsRegisteredAsASingleton()
    {
        var source = ReadMcpProgramSource();
        var registered = AddSingletonPattern.Matches(source)
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = DiscoverToolTypes()
            .Select(type => type.Name)
            .Where(name => !registered.Contains(name))
            .ToList();

        missing.ShouldBeEmpty(
            "Tool classes missing AddSingleton<T>() in Twig.Mcp/Program.cs. WithTools<T>() "
            + "registers discovery metadata but does NOT add the type to the container, so "
            + $"every call to these tools fails at runtime: {string.Join(", ", missing)}");
    }

    /// <summary>Touch point 2: MCP discovery metadata.</summary>
    [Fact]
    public void EveryToolType_IsRegisteredWithWithTools()
    {
        var source = ReadMcpProgramSource();
        var advertised = WithToolsPattern.Matches(source)
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = DiscoverToolTypes()
            .Select(type => type.Name)
            .Where(name => !advertised.Contains(name))
            .ToList();

        missing.ShouldBeEmpty(
            "Tool classes missing .WithTools<T>() in Twig.Mcp/Program.cs. They are in the "
            + "container but never advertised, so no MCP client can discover or call them: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// Touch point 1 ∩ 2: the two lists must be exactly the same set. A name in one
    /// and not the other is the drift that produced the original bugs.
    /// </summary>
    [Fact]
    public void SingletonAndWithToolsRegistrations_AreTheSameSet()
    {
        var source = ReadMcpProgramSource();

        var singletons = AddSingletonPattern.Matches(source)
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var advertised = WithToolsPattern.Matches(source)
            .Select(match => match.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        singletons.Count.ShouldBeGreaterThan(5, "Source scrape went vacuous.");

        var singletonOnly = singletons.Except(advertised).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var advertisedOnly = advertised.Except(singletons).OrderBy(n => n, StringComparer.Ordinal).ToList();

        singletonOnly.ShouldBeEmpty(
            $"In the container but never advertised: {string.Join(", ", singletonOnly)}");
        advertisedOnly.ShouldBeEmpty(
            $"Advertised but not in the container — calls will throw: {string.Join(", ", advertisedOnly)}");
    }

    /// <summary>
    /// Touch point 3: the catalog. Every declared tool name must be in
    /// <see cref="McpToolCatalog.AllToolNames"/>, and the catalog must contain no
    /// names that no longer have a method behind them.
    /// </summary>
    [Fact]
    public void AllToolNames_ExactlyMatchesDeclaredToolMethods()
    {
        var declared = DiscoverToolTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        declared.Count.ShouldBeGreaterThan(20,
            "Tool-method scrape went vacuous — [McpServerTool(Name = ...)] discovery broke.");

        var missingFromCatalog = declared
            .Except(McpToolCatalog.AllToolNames)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        missingFromCatalog.ShouldBeEmpty(
            "Tool methods missing from McpToolCatalog.AllToolNames. They are invisible in "
            + "every profile and cannot be used inside twig_batch: "
            + string.Join(", ", missingFromCatalog));

        var orphanedCatalogEntries = McpToolCatalog.AllToolNames
            .Except(declared)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        orphanedCatalogEntries.ShouldBeEmpty(
            "AllToolNames advertises tools with no [McpServerTool] method behind them: "
            + string.Join(", ", orphanedCatalogEntries));
    }

    /// <summary>
    /// The catalog's derived sets must stay inside <c>AllToolNames</c>. A typo in
    /// any of them silently produces a name that matches nothing.
    /// </summary>
    [Fact]
    public void DerivedCatalogSets_AreSubsetsOfAllToolNames()
    {
        var compactStrays = McpToolCatalog.CompactToolNames
            .Except(McpToolCatalog.AllToolNames)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        compactStrays.ShouldBeEmpty(
            $"CompactToolNames entries not in AllToolNames: {string.Join(", ", compactStrays)}");

        var batchableStrays = McpToolCatalog.BatchableToolNames
            .Except(McpToolCatalog.AllToolNames)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        batchableStrays.ShouldBeEmpty(
            $"BatchableToolNames entries not in AllToolNames: {string.Join(", ", batchableStrays)}");
    }

    /// <summary>
    /// Not just "does it resolve?" — .NET DI picks the greediest SATISFIABLE
    /// constructor, so a tool whose dependency is unregistered still resolves via a
    /// narrower overload and degrades silently. Assert every constructor parameter
    /// of every tool type is actually resolvable.
    /// </summary>
    [Fact]
    public void EveryToolType_HasAllConstructorDependenciesRegistered()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigRoot = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigRoot);

            var registry = new WorkspaceRegistry(twigRoot);
            var authProvider = NSubstitute.Substitute.For<Twig.Domain.Interfaces.IAuthenticationProvider>();
            using var httpClient = new HttpClient();
            var factory = new WorkspaceContextFactory(registry, httpClient, authProvider, twigRoot);
            var resolver = new WorkspaceResolver(registry, factory);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IWorkspaceRegistry>(registry);
            services.AddSingleton(resolver);
            services.AddSingleton<Twig.Domain.Interfaces.ISeedIdCounter, Twig.Domain.Services.Seed.SeedIdCounter>();
            services.AddSingleton<Twig.Domain.Services.Seed.SeedFactory>();
            services.AddSingleton<Twig.Mcp.Services.Batch.IToolDispatcher, Twig.Mcp.Services.Batch.ToolDispatcher>();

            var toolTypes = DiscoverToolTypes();
            foreach (var type in toolTypes)
                services.AddSingleton(type);

            using var provider = services.BuildServiceProvider();

            var degraded = new List<string>();

            foreach (var type in toolTypes)
            {
                var widest = type.GetConstructors()
                    .OrderByDescending(ctor => ctor.GetParameters().Length)
                    .FirstOrDefault();
                if (widest is null) continue;

                foreach (var parameter in widest.GetParameters())
                {
                    if (parameter.ParameterType == typeof(string)
                        || parameter.ParameterType.IsPrimitive)
                    {
                        continue;
                    }

                    if (provider.GetService(parameter.ParameterType) is null)
                        degraded.Add($"{type.Name}.{parameter.Name} ({parameter.ParameterType.Name})");
                }
            }

            degraded.ShouldBeEmpty(
                "Unregistered MCP tool constructor dependencies. Because .NET DI selects the "
                + "greediest SATISFIABLE constructor, the tool still resolves — via a DEGRADED "
                + $"overload — and fails silently at call time: {string.Join(", ", degraded)}");

            factory.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
