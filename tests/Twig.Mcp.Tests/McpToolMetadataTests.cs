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

        // 40 before wayfinder 0021 removed twig_set, twig_parent, and twig_children.
        // 38 since twig_process_description (AB#241) added the named-type agent surface.
        // 44 since wayfinder 0022 added the plan lifecycle surface (twig_plan_validate,
        // twig_plan_preview, twig_plan_apply, twig_plan_status, twig_plan_seed) plus
        // twig_pending.
        // 49 since AB#742 renamed that surface to twig_proposal_* and kept the five
        // twig_plan_* spellings as deprecated aliases. The aliases are real registered tools
        // with their own parameter lists, so they legitimately raise this count by five; an
        // alias that stopped carrying `workspace` would drop it back and fail here.
        parameters.Count.ShouldBe(49);
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
