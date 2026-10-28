using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Twig.Domain.Aggregates;
using Xunit;

namespace Twig.Domain.Tests.Architecture;

public sealed class ProviderContractBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string> LegacyCompatibility = new Dictionary<string, string>
    {
        ["Twig.Domain.Interfaces.IAdoWorkItemService"] = "83f7bb342beb49952bbc83cc1b6c9b58ba00b75575316da17c0ccc9a11c7b576",
        ["Twig.Domain.Interfaces.IRevisionBoundAdoWorkItemService"] = "2cd61874239d71f98ca4bf0d8d9b0ad642a066d503ea4947eec3de7e2753b2e4",
        ["Twig.Domain.Interfaces.IAdoGitService"] = "a82280a0c941de399664eea735cfd7d81b904c1f9d90d3b52cf69183e909fe09",
        ["Twig.Domain.Services.Mutation.DeleteOutcome+AdoFailed"] = "d0a365a81708f7156b1d02a2c8b4cea5cfeb2524c9ae2b695d07c321191f4424",
        ["Twig.Domain.Services.Mutation.PatchOutcome+AdoUnreachable"] = "cdead0fb4527b1606854aa0a2d1f4f64bdfeb8b23ed9aad0b880e3ea64779903",
        ["Twig.Domain.Services.Navigation.FetchedFromAdo"] = "5afb81a7f2ff110e16be593f679f7f34dad5218880029a73e4510458c2a9bf47",
    };

    private static readonly IReadOnlyDictionary<string, string> SelfUpdateOnly = new Dictionary<string, string>
    {
        ["Twig.Domain.Interfaces.IGitHubReleaseService"] = "1499172b6f1cb6580756d6e100cf901f3dc624d8a1c2ac88fa82c1b975b6ce23",
        ["Twig.Domain.Interfaces.GitHubReleaseInfo"] = "addf28f68e24cb07bb37f2ef4a9f63d29947bf50d94d80ee6c52f2b0417a9bad",
        ["Twig.Domain.Interfaces.GitHubReleaseAssetInfo"] = "889dc9c876794aaac673135d2db33b27904ebfcd9f1575025cf5f2013322f90c",
    };

    [Fact]
    public void ProviderNamedDomainTypesAreOnlyExactReviewedExceptions()
    {
        var actual = typeof(WorkItem).Assembly.GetTypes()
            .Where(type => HasProviderName(type.Name))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(LegacyCompatibility.Keys.Concat(SelfUpdateOnly.Keys).Order(StringComparer.Ordinal).ToArray(),
            "New provider work-management contracts belong in Infrastructure. See docs/architecture/provider-contract-boundary.md.");
    }

    public static TheoryData<string, string> ReviewedExceptions()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, expected) in LegacyCompatibility.Concat(SelfUpdateOnly))
            data.Add(name, expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReviewedExceptions))]
    public void ReviewedExceptionsCannotGrowOrChangeTheirContractShape(string name, string expected)
    {
        var type = typeof(WorkItem).Assembly.GetType(name).ShouldNotBeNull();
        var shape = Shape(type);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)));
        fingerprint.ShouldBe(expected, $"{name}\n{shape}\nActual fingerprint: {fingerprint}");
    }

    [Theory]
    [InlineData("IGitHubWorkItemClient", true)]
    [InlineData("IRevisionBoundAdoWorkItemService", true)]
    [InlineData("JiraSnapshot", true)]
    [InlineData("GitLabWorkItem", true)]
    [InlineData("ReadOnlySnapshot", false)]
    [InlineData("IProviderWorkItemReader", false)]
    public void ProviderNameGuardHasPositiveAndNegativeControls(string name, bool expected) =>
        HasProviderName(name).ShouldBe(expected);

    [Fact]
    public void MemberShapeIncludesInheritedMembersAndOptionalDefaults()
    {
        Shape(typeof(IFirstDefault)).ShouldNotBe(Shape(typeof(ISecondDefault)));
        Shape(typeof(IInheritedMethod)).ShouldNotBe(Shape(typeof(INoInheritedMethod)));
        Shape(typeof(IFirstDefault)).ShouldBe(Shape(typeof(IFirstDefault)));
    }

    private interface IFirstDefault { void Run(int count = 1); }
    private interface ISecondDefault { void Run(int count = 2); }
    private interface IInheritedMethod : IFirstDefault { }
    private interface INoInheritedMethod { }

    private static bool HasProviderName(string name) =>
        new[] { "Ado", "ADO", "GitHub", "Github", "GitLab", "Gitlab", "AzureDevOps", "Jira" }
            .Any(marker => name.Contains(marker, StringComparison.Ordinal));

    private static string Shape(Type type)
    {
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(MemberShape)
            .Concat(type.GetInterfaces().SelectMany(contract => contract.GetMembers()
                .Select(member => $"{contract}:{MemberShape(member)}")))
            .Order(StringComparer.Ordinal);
        return $"{type.Attributes}:{type.BaseType}\n"
            + string.Join("\n", type.GetInterfaces().Select(contract => contract.ToString()).Order(StringComparer.Ordinal))
            + "\n" + string.Join("\n", members);
    }

    private static string MemberShape(MemberInfo member) =>
        member.MemberType + ":" + member + (member switch
        {
            MethodBase method => $":{method.Attributes}:{method.CallingConvention}:"
                + string.Join(";", method.GetParameters().Select(parameter =>
                    $"{parameter.Name}:{parameter.Attributes}:{Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture)}")),
            FieldInfo field => ":" + field.Attributes,
            _ => "",
        });
}
