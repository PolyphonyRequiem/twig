using System.Text.Json;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Serialization;

/// <summary>
/// Tests for <see cref="TwigJsonContext"/> source-gen registrations.
/// </summary>
public class TwigJsonContextTests
{
    [Fact]
    public void SeedPublishRules_IsRegisteredInTwigJsonContext()
    {
        // Verify that the source-gen context includes SeedPublishRules for AOT compatibility
        TwigJsonContext.Default.SeedPublishRules.ShouldNotBeNull();
    }

    [Fact]
    public void DescendantVerificationResult_IsRegisteredInTwigJsonContext()
    {
        TwigJsonContext.Default.DescendantVerificationResult.ShouldNotBeNull();
    }

    [Fact]
    public void ProviderNeutralTypes_AreRegisteredInTwigJsonContext()
    {
        TwigJsonContext.Default.ExternalWorkItemIdentity.ShouldNotBeNull();
        TwigJsonContext.Default.ProviderRevisionToken.ShouldNotBeNull();
        TwigJsonContext.Default.ProviderWorkItemRelationship.ShouldNotBeNull();
    }

    [Fact]
    public void ProviderWorkItemRelationship_RoundTripsWithCanonicalLinkKindName()
    {
        var relationship = new ProviderWorkItemRelationship(
            new ExternalWorkItemIdentity("github", "https://api.github.com", "I_kwDOExample"),
            new ExternalWorkItemIdentity("github", "https://api.github.com", "I_kwDOAnother"),
            LinkKind.PredecessorSuccessor);

        var json = JsonSerializer.Serialize(relationship, TwigJsonContext.Default.ProviderWorkItemRelationship);
        json.ShouldContain("\"kind\":\"predecessor-successor\"");

        var deserialized = JsonSerializer.Deserialize(
            json,
            TwigJsonContext.Default.ProviderWorkItemRelationship);

        deserialized.ShouldBe(relationship);
    }

    [Fact]
    public void DescendantVerificationResult_RoundTrips_WithVerifiedTrue()
    {
        var result = new DescendantVerificationResult(
            RootId: 42,
            Verified: true,
            TotalChecked: 5,
            Incomplete: []);

        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DescendantVerificationResult);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.DescendantVerificationResult);

        deserialized.ShouldNotBeNull();
        deserialized.RootId.ShouldBe(42);
        deserialized.Verified.ShouldBeTrue();
        deserialized.TotalChecked.ShouldBe(5);
        deserialized.Incomplete.ShouldBeEmpty();
    }

    [Fact]
    public void DescendantVerificationResult_RoundTrips_WithIncompleteItems()
    {
        var incomplete = new IncompleteItem(
            Id: 101,
            Title: "Fix bug",
            Type: "Task",
            State: "Doing",
            ParentId: 42,
            Depth: 2);

        var result = new DescendantVerificationResult(
            RootId: 42,
            Verified: false,
            TotalChecked: 3,
            Incomplete: [incomplete]);

        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DescendantVerificationResult);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.DescendantVerificationResult);

        deserialized.ShouldNotBeNull();
        deserialized.Verified.ShouldBeFalse();
        deserialized.Incomplete.Count.ShouldBe(1);

        var item = deserialized.Incomplete[0];
        item.Id.ShouldBe(101);
        item.Title.ShouldBe("Fix bug");
        item.Type.ShouldBe("Task");
        item.State.ShouldBe("Doing");
        item.ParentId.ShouldBe(42);
        item.Depth.ShouldBe(2);
    }

    [Fact]
    public void IncompleteItem_NullParentId_SerializesCorrectly()
    {
        var item = new IncompleteItem(
            Id: 200,
            Title: "Root child",
            Type: "Issue",
            State: "To Do",
            ParentId: null,
            Depth: 1);

        var result = new DescendantVerificationResult(
            RootId: 1,
            Verified: false,
            TotalChecked: 1,
            Incomplete: [item]);

        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DescendantVerificationResult);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.DescendantVerificationResult);

        deserialized.ShouldNotBeNull();
        deserialized.Incomplete[0].ParentId.ShouldBeNull();
    }
}
