using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="TrackingFile"/> and <see cref="TrackingFileEntry"/> POCO models
/// and their TwigJsonContext registrations.
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
    public void TrackingFile_DefaultsToEmptyTrackedCollection()
    {
        var file = new TrackingFile();

        file.Tracked.ShouldNotBeNull();
        file.Tracked.ShouldBeEmpty();
    }

    [Fact]
    public void TrackingFile_RoundTrips_EmptyFile_OmitsExcludedProperty()
    {
        var file = new TrackingFile();

        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.ShouldBeEmpty();
        json.ShouldNotContain("\"excluded\"");
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
        json.ShouldNotContain("\"excluded\"");
    }
}