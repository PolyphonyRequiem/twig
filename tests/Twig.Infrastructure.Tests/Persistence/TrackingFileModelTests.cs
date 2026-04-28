using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="TrackingFile"/>, <see cref="TrackingFileEntry"/>,
/// and <see cref="ExclusionFileEntry"/> POCO models and their TwigJsonContext registrations.
/// </summary>
public sealed class TrackingFileModelTests
{
    [Fact]
    public void TrackingFile_IsRegisteredInTwigJsonContext()
    {
        TwigJsonContext.Default.TrackingFile.ShouldNotBeNull();
    }

    [Fact]
    public void TrackingFileEntry_IsRegisteredInTwigJsonContext()
    {
        TwigJsonContext.Default.TrackingFileEntry.ShouldNotBeNull();
    }

    [Fact]
    public void ExclusionFileEntry_IsRegisteredInTwigJsonContext()
    {
        TwigJsonContext.Default.ExclusionFileEntry.ShouldNotBeNull();
    }

    [Fact]
    public void TrackingFile_DefaultsToEmptyCollections()
    {
        var file = new TrackingFile();

        file.Tracked.ShouldNotBeNull();
        file.Tracked.ShouldBeEmpty();
        file.Excluded.ShouldNotBeNull();
        file.Excluded.ShouldBeEmpty();
    }

    [Fact]
    public void TrackingFileEntry_DefaultValues()
    {
        var entry = new TrackingFileEntry();

        entry.Id.ShouldBe(0);
        entry.Mode.ShouldBe("single");
        entry.AddedAt.ShouldBe(string.Empty);
    }

    [Fact]
    public void ExclusionFileEntry_DefaultValues()
    {
        var entry = new ExclusionFileEntry();

        entry.Id.ShouldBe(0);
        entry.AddedAt.ShouldBe(string.Empty);
    }

    [Fact]
    public void TrackingFile_RoundTrips_EmptyFile()
    {
        var file = new TrackingFile();

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.ShouldBeEmpty();
        deserialized.Excluded.ShouldBeEmpty();
    }

    [Fact]
    public void TrackingFile_RoundTrips_WithTrackedEntries()
    {
        var file = new TrackingFile
        {
            Tracked =
            [
                new TrackingFileEntry { Id = 42, Mode = "single", AddedAt = "2026-04-28T12:00:00Z" },
                new TrackingFileEntry { Id = 99, Mode = "tree", AddedAt = "2026-04-28T13:00:00Z" }
            ]
        };

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(2);
        deserialized.Tracked[0].Id.ShouldBe(42);
        deserialized.Tracked[0].Mode.ShouldBe("single");
        deserialized.Tracked[0].AddedAt.ShouldBe("2026-04-28T12:00:00Z");
        deserialized.Tracked[1].Id.ShouldBe(99);
        deserialized.Tracked[1].Mode.ShouldBe("tree");
    }

    [Fact]
    public void TrackingFile_RoundTrips_WithExcludedEntries()
    {
        var file = new TrackingFile
        {
            Excluded =
            [
                new ExclusionFileEntry { Id = 7, AddedAt = "2026-04-28T14:00:00Z" }
            ]
        };

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Excluded.Count.ShouldBe(1);
        deserialized.Excluded[0].Id.ShouldBe(7);
        deserialized.Excluded[0].AddedAt.ShouldBe("2026-04-28T14:00:00Z");
    }

    [Fact]
    public void TrackingFile_RoundTrips_WithBothTrackedAndExcluded()
    {
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 1, Mode = "tree", AddedAt = "2026-01-01T00:00:00Z" }],
            Excluded = [new ExclusionFileEntry { Id = 2, AddedAt = "2026-01-02T00:00:00Z" }]
        };

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(1);
        deserialized.Excluded.Count.ShouldBe(1);
    }

    [Fact]
    public void TrackingFile_SerializesWithCamelCasePropertyNames()
    {
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 1, Mode = "single", AddedAt = "2026-01-01T00:00:00Z" }],
            Excluded = [new ExclusionFileEntry { Id = 2, AddedAt = "2026-01-02T00:00:00Z" }]
        };

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);

        json.ShouldContain("\"tracked\"");
        json.ShouldContain("\"excluded\"");
        json.ShouldContain("\"id\"");
        json.ShouldContain("\"mode\"");
        json.ShouldContain("\"addedAt\"");
    }
}
