using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Xunit;

namespace Twig.Domain.Tests.Capabilities;

public sealed class CapabilityContractTests
{
    private static RelationshipCapability Native(LinkKind kind) =>
        new(kind, CapabilityImplementation.Native, EmulationStorage.None);

    private static ProviderCapabilityDescriptor Descriptor(params RelationshipCapability[] caps) =>
        new("test", caps);

    [Fact]
    public void Descriptor_AcceptsCompleteDeclaration_AndCopiesInput()
    {
        var caps = new List<RelationshipCapability>
        {
            Native(LinkKind.ParentChild),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Emulated, EmulationStorage.LocalOnly),
            new(LinkKind.Related, CapabilityImplementation.Unsupported, EmulationStorage.None),
        };

        var descriptor = new ProviderCapabilityDescriptor("test", caps);
        caps.Clear();

        descriptor.Relationships.Count.ShouldBe(3);
        descriptor.For(LinkKind.PredecessorSuccessor).Storage.ShouldBe(EmulationStorage.LocalOnly);
    }

    private static readonly (string Provider, RelationshipCapability[] Caps, string Expected)[] InvalidCases =
    [
        (" ", [Native(LinkKind.ParentChild), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related)], "provider kind is required"),
        ("p", [Native(LinkKind.ParentChild), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related), Native(LinkKind.Artifact)], "artifact"),
        ("p", [Native(LinkKind.ParentChild), Native(LinkKind.PredecessorSuccessor), Native((LinkKind)99)], "undefined value"),
        ("p", [Native(LinkKind.ParentChild), Native(LinkKind.ParentChild), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related)], "more than once"),
        ("p", [new(LinkKind.ParentChild, CapabilityImplementation.Emulated, EmulationStorage.None), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related)], "must declare its storage"),
        ("p", [new(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.LocalOnly), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related)], "cannot declare emulation storage"),
        ("p", [new(LinkKind.ParentChild, CapabilityImplementation.Unsupported, EmulationStorage.LocalWithProviderProjection), Native(LinkKind.PredecessorSuccessor), Native(LinkKind.Related)], "cannot declare emulation storage"),
        ("p", [Native(LinkKind.ParentChild), Native(LinkKind.PredecessorSuccessor)], "is not declared"),
    ];

    public static TheoryData<int> InvalidCaseIndexes => new(Enumerable.Range(0, InvalidCases.Length));

    [Theory]
    [MemberData(nameof(InvalidCaseIndexes))]
    public void Descriptor_RejectsInvalidDeclarations(int index)
    {
        var (provider, caps, expected) = InvalidCases[index];
        var ex = Should.Throw<ArgumentException>(() => new ProviderCapabilityDescriptor(provider, caps));
        ex.Message.ShouldStartWith("capability-descriptor-invalid:");
        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public void Catalog_DeclaresNativeAdoAndGitHub_WithNothingEmulated()
    {
        ProviderCapabilityCatalog.Ado.Relationships.ShouldAllBe(c => c.Implementation == CapabilityImplementation.Native);

        var gh = ProviderCapabilityCatalog.GitHub;
        gh.For(LinkKind.ParentChild).Implementation.ShouldBe(CapabilityImplementation.Native);
        gh.For(LinkKind.PredecessorSuccessor).Implementation.ShouldBe(CapabilityImplementation.Native);
        gh.For(LinkKind.Related).Implementation.ShouldBe(CapabilityImplementation.Unsupported);
        gh.Relationships.ShouldAllBe(c => c.Storage == EmulationStorage.None);
    }

    [Theory]
    [InlineData("ado", true)]
    [InlineData("github", true)]
    [InlineData("ADO", false)]
    [InlineData("GitHub", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Catalog_TryGet_IsOrdinal(string? kind, bool found)
    {
        ProviderCapabilityCatalog.TryGet(kind, out var descriptor).ShouldBe(found);
        if (found)
            descriptor.ProviderKind.ShouldBe(kind);
    }

    [Fact]
    public void Report_WithoutEvidence_IsUnknownNotProbed_NeverUnsupported()
    {
        var report = CapabilityReport.Create(ProviderCapabilityCatalog.Ado);

        report.ProviderKind.ShouldBe("ado");
        report.Relationships.Count.ShouldBe(3);
        report.Relationships.ShouldAllBe(e =>
            e.Availability == CapabilityAvailability.Unknown
            && e.ReasonCode == CapabilityReport.NotProbedReason
            && e.Implementation == CapabilityImplementation.Native);
    }

    [Fact]
    public void Report_DeniedProbe_KeepsDeclaredImplementation()
    {
        var report = CapabilityReport.Create(
            ProviderCapabilityCatalog.GitHub,
            [new(LinkKind.ParentChild, CapabilityAvailability.Unavailable, "permission-denied")]);

        var entry = report.Relationships.Single(e => e.Kind == LinkKind.ParentChild);
        entry.Implementation.ShouldBe(CapabilityImplementation.Native);
        entry.Availability.ShouldBe(CapabilityAvailability.Unavailable);
        entry.ReasonCode.ShouldBe("permission-denied");
    }

    [Fact]
    public void Report_RejectsUndeclaredOrDuplicateEvidence()
    {
        Should.Throw<ArgumentException>(() => CapabilityReport.Create(
                ProviderCapabilityCatalog.Ado,
                [new(LinkKind.Artifact, CapabilityAvailability.Available, null)]))
            .Message.ShouldStartWith("capability-evidence-invalid:");

        Should.Throw<ArgumentException>(() => CapabilityReport.Create(
                ProviderCapabilityCatalog.Ado,
                [
                    new(LinkKind.Related, CapabilityAvailability.Available, null),
                    new(LinkKind.Related, CapabilityAvailability.Unavailable, null),
                ]))
            .Message.ShouldContain("more than one");
    }

    [Fact]
    public void Routing_FollowsDeclarationOnly()
    {
        var descriptor = Descriptor(
            Native(LinkKind.ParentChild),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Emulated, EmulationStorage.LocalWithProviderProjection),
            new(LinkKind.Related, CapabilityImplementation.Unsupported, EmulationStorage.None));

        RelationshipRouting.Decide(descriptor, LinkKind.ParentChild)
            .ShouldBe(new RelationshipRoute(RelationshipRouteKind.Native, EmulationStorage.None, null));
        RelationshipRouting.Decide(descriptor, LinkKind.PredecessorSuccessor)
            .ShouldBe(new RelationshipRoute(RelationshipRouteKind.Emulated, EmulationStorage.LocalWithProviderProjection, null));
        RelationshipRouting.Decide(descriptor, LinkKind.Related)
            .ShouldBe(new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.UnsupportedReason));
        RelationshipRouting.Decide(descriptor, LinkKind.Artifact)
            .ShouldBe(new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.NotARelationshipReason));
    }

    [Theory]
    [InlineData(LinkKind.ParentChild, "parent-child")]
    [InlineData(LinkKind.PredecessorSuccessor, "predecessor-successor")]
    [InlineData(LinkKind.Related, "related")]
    public void LinkKindTokens_RoundTrip(LinkKind kind, string token)
    {
        LinkKindTokens.ToToken(kind).ShouldBe(token);
        LinkKindTokens.TryResolve(token, out var resolved).ShouldBeTrue();
        resolved.ShouldBe(kind);
    }

    [Theory]
    [InlineData("Related")]
    [InlineData("ParentChild")]
    [InlineData("1")]
    [InlineData(" related")]
    [InlineData(null)]
    public void LinkKindTokens_RejectNonCanonical(string? token) =>
        LinkKindTokens.TryResolve(token, out _).ShouldBeFalse();

    [Fact]
    public void CapabilityTokens_AreCanonicalAndComplete()
    {
        TokensOf(CapabilityTokens.Implementation).ShouldBe(["native", "emulated", "unsupported"]);
        TokensOf(CapabilityTokens.Availability).ShouldBe(["available", "unavailable", "unknown"]);
        TokensOf(CapabilityTokens.Storage).ShouldBe(["none", "local-only", "local-with-provider-projection"]);
        TokensOf(CapabilityTokens.Origin).ShouldBe(["provider-native", "twig-local"]);
        TokensOf(CapabilityTokens.Publication).ShouldBe(["not-applicable", "pending", "published", "verified", "conflict"]);

        CapabilityTokens.Implementation.Length.ShouldBe(Enum.GetValues<CapabilityImplementation>().Length);
        CapabilityTokens.Availability.Length.ShouldBe(Enum.GetValues<CapabilityAvailability>().Length);
        CapabilityTokens.Storage.Length.ShouldBe(Enum.GetValues<EmulationStorage>().Length);
        CapabilityTokens.Origin.Length.ShouldBe(Enum.GetValues<RelationshipOrigin>().Length);
        CapabilityTokens.Publication.Length.ShouldBe(Enum.GetValues<PublicationState>().Length);
    }

    [Theory]
    [InlineData("local-with-provider-projection", true)]
    [InlineData("LocalWithProviderProjection", false)]
    [InlineData("2", false)]
    public void CapabilityTokens_TryResolve_IsStrict(string token, bool ok)
    {
        CapabilityTokens.TryResolve(CapabilityTokens.Storage, token, out var value).ShouldBe(ok);
        if (ok)
            value.ShouldBe(EmulationStorage.LocalWithProviderProjection);
    }

    [Fact]
    public void JsonConverter_WritesTokenAndRejectsClrName()
    {
        var converter = new EmulationStorageJsonConverter();
        var options = new JsonSerializerOptions();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            converter.Write(writer, EmulationStorage.LocalOnly, options);
        Encoding.UTF8.GetString(stream.ToArray()).ShouldBe("\"local-only\"");

        ReadStorage(converter, "\"local-only\"", options).ShouldBe(EmulationStorage.LocalOnly);
        Should.Throw<JsonException>(() => ReadStorage(converter, "\"LocalOnly\"", options));
        Should.Throw<JsonException>(() => ReadStorage(converter, "1", options));
    }

    private static EmulationStorage ReadStorage(EmulationStorageJsonConverter converter, string json, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return converter.Read(ref reader, typeof(EmulationStorage), options);
    }

    private static string[] TokensOf<T>((T Value, string Token)[] map) => map.Select(m => m.Token).ToArray();
}
