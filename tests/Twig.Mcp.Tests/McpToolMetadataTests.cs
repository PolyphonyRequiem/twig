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
        typeof(PlanTools),
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
