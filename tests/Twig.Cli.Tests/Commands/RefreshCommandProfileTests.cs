using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for global profile metadata updates during <c>twig refresh</c>.
/// Covers hash drift detection, LastSyncedAt updates, and FR-09 fault isolation.
/// </summary>
public class RefreshCommandProfileTests : RefreshCommandTestBase
{
    private readonly IGlobalProfileStore _globalProfileStore;

    private static readonly IReadOnlyList<FieldDefinition> TestFields =
    [
        new("System.Title", "Title", "string", false),
        new("System.State", "State", "string", false),
        new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
    ];

    private static readonly string TestFieldHash = FieldDefinitionHasher.ComputeFieldHash(TestFields);

    public RefreshCommandProfileTests()
    {
        _config.ProcessTemplate = "Agile";
        _globalProfileStore = Substitute.For<IGlobalProfileStore>();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(TestFields);
    }

    private RefreshCommand CreateCommand(TextWriter? stderr = null) =>
        CreateRefreshCommand(stderr, _globalProfileStore);

    [Fact]
    public async Task Refresh_HashUnchanged_UpdatesLastSyncedAtOnly()
    {
        var originalCreatedAt = DateTimeOffset.UtcNow.AddDays(-7);
        var originalSyncedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var existingMetadata = new ProfileMetadata(
            "https://dev.azure.com/org", originalCreatedAt, originalSyncedAt,
            TestFieldHash, TestFields.Count);

        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingMetadata);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.Received(1).SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<ProfileMetadata>(m =>
                m.FieldDefinitionHash == TestFieldHash &&
                m.FieldCount == TestFields.Count &&
                m.CreatedAt == originalCreatedAt &&
                m.LastSyncedAt > originalSyncedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_HashChanged_UpdatesMetadataAndEmitsDriftWarning()
    {
        var originalCreatedAt = DateTimeOffset.UtcNow.AddDays(-7);
        var oldHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var existingMetadata = new ProfileMetadata(
            "https://dev.azure.com/org", originalCreatedAt,
            DateTimeOffset.UtcNow.AddHours(-1), oldHash, 2);

        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingMetadata);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Verify metadata saved with new hash and updated field count
        await _globalProfileStore.Received(1).SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<ProfileMetadata>(m =>
                m.FieldDefinitionHash == TestFieldHash &&
                m.FieldCount == TestFields.Count &&
                m.CreatedAt == originalCreatedAt),
            Arg.Any<CancellationToken>());

        // Verify drift warning emitted to stderr
        stderr.ToString().ShouldContain("Field definitions changed since last profile sync");
    }

    [Fact]
    public async Task Refresh_NoProfileExists_NoActionTaken()
    {
        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProfileMetadata?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.DidNotReceive().SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ProfileIoFailure_ContinuesNormally()
    {
        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // FR-09: refresh must succeed regardless of profile I/O errors
        result.ShouldBe(0);
    }
}
