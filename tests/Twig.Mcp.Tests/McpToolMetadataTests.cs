using System.ComponentModel;
using System.Reflection;
using Shouldly;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

public sealed class McpToolMetadataTests
{
    private static readonly Type[] ToolTypes =
    [
        typeof(AdminTools),
        typeof(BatchTools),
        typeof(CreationTools),
        typeof(MutationTools),
        typeof(NavigationTools),
        typeof(ProcessTools),
        typeof(ReadTools),
        typeof(SeedTools),
        typeof(TrackingTools),
        typeof(WorkspaceTools),
    ];

    [Fact]
    public void WorkspaceParameters_RecommendOmissionAndShareCanonicalDescription()
    {
        var parameters = ToolTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.Name == "workspace")
            .ToList();

        // 40 before wayfinder 0021 removed twig_set, twig_parent, and twig_children.
        parameters.Count.ShouldBe(37);
        McpToolDescriptions.WorkspaceOverride.ShouldContain("Omit");
        McpToolDescriptions.WorkspaceOverride.ShouldContain("repo-local");

        foreach (var parameter in parameters)
        {
            parameter.HasDefaultValue.ShouldBeTrue();
            parameter.DefaultValue.ShouldBeNull();

            var description = parameter.GetCustomAttribute<DescriptionAttribute>();
            description.ShouldNotBeNull();

            var expected = parameter.Member.DeclaringType == typeof(BatchTools)
                ? McpToolDescriptions.BatchWorkspaceOverride
                : McpToolDescriptions.WorkspaceOverride;
            description.Description.ShouldBe(expected);
        }
    }
}
