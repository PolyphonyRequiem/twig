using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Integration tests for <see cref="GlobalProfileStore"/> using temp directories.
/// Covers round-trip serialization, missing/corrupt files, lazy directory creation,
/// and concurrent write safety.
/// </summary>
public class GlobalProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GlobalProfileStore _store;

    public GlobalProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _store = new GlobalProfileStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ──────────────────────── LoadStatusFieldsAsync ────────────────────────

    [Fact]
    public async Task LoadStatusFieldsAsync_ReturnsNull_WhenProfileMissing()
    {
        var result = await _store.LoadStatusFieldsAsync("org", "process");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task StatusFields_SaveAndLoad_RoundTrips()
    {
        const string content = "System.State\nSystem.Reason\nMicrosoft.VSTS.Common.Priority";

        await _store.SaveStatusFieldsAsync("myorg", "Agile", content);
        var loaded = await _store.LoadStatusFieldsAsync("myorg", "Agile");

        loaded.ShouldBe(content);
    }

    // ──────────────────────── LoadMetadataAsync ────────────────────────

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNull_WhenProfileMissing()
    {
        var result = await _store.LoadMetadataAsync("org", "process");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Metadata_SaveAndLoad_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new ProfileMetadata(
            "myorg", now, now, "sha256:abc123", 47);

        await _store.SaveMetadataAsync("myorg", "Agile", metadata);
        var loaded = await _store.LoadMetadataAsync("myorg", "Agile");

        loaded.ShouldNotBeNull();
        loaded.Organization.ShouldBe("myorg");
        loaded.CreatedAt.ShouldBe(now);
        loaded.LastSyncedAt.ShouldBe(now);
        loaded.FieldDefinitionHash.ShouldBe("sha256:abc123");
        loaded.FieldCount.ShouldBe(47);
    }

    [Fact]
    public async Task Metadata_SaveAndLoad_ProducesValidJson()
    {
        var metadata = new ProfileMetadata(
            "myorg",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "sha256:deadbeef", 10);

        await _store.SaveMetadataAsync("myorg", "Scrum", metadata);

        // Read the raw JSON and verify it's valid
        var path = Path.Combine(_tempDir, "myorg", "Scrum", "profile.json");
        File.Exists(path).ShouldBeTrue();
        var json = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("organization").GetString().ShouldBe("myorg");
        doc.RootElement.GetProperty("fieldCount").GetInt32().ShouldBe(10);
    }

    // ──────────────────────── Lazy directory creation ────────────────────────

    [Fact]
    public async Task SaveStatusFieldsAsync_CreatesDirectoryTreeLazily()
    {
        var profileDir = Path.Combine(_tempDir, "neworg", "newprocess");
        Directory.Exists(profileDir).ShouldBeFalse();

        await _store.SaveStatusFieldsAsync("neworg", "newprocess", "content");

        Directory.Exists(profileDir).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveMetadataAsync_CreatesDirectoryTreeLazily()
    {
        var profileDir = Path.Combine(_tempDir, "neworg", "newprocess");
        Directory.Exists(profileDir).ShouldBeFalse();

        var metadata = new ProfileMetadata(
            "neworg",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "sha256:000", 1);

        await _store.SaveMetadataAsync("neworg", "newprocess", metadata);

        Directory.Exists(profileDir).ShouldBeTrue();
    }

    // ──────────────────────── Corrupt file handling ────────────────────────

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNull_WhenJsonIsCorrupt()
    {
        // Write corrupt JSON directly to the expected path
        var profileDir = Path.Combine(_tempDir, "myorg", "Agile");
        Directory.CreateDirectory(profileDir);
        await File.WriteAllTextAsync(Path.Combine(profileDir, "profile.json"), "{ not valid json }");

        var result = await _store.LoadMetadataAsync("myorg", "Agile");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task LoadStatusFieldsAsync_ReturnsContent_EvenIfMalformed()
    {
        // Status fields are raw text — no JSON parsing, so "corrupt" text is still valid
        var profileDir = Path.Combine(_tempDir, "myorg", "Agile");
        Directory.CreateDirectory(profileDir);
        await File.WriteAllTextAsync(Path.Combine(profileDir, "status-fields"), "garbage\x00data");

        var result = await _store.LoadStatusFieldsAsync("myorg", "Agile");

        result.ShouldNotBeNull();
    }

    // ──────────────────────── Concurrent saves ────────────────────────

    [Fact]
    public async Task ConcurrentSaves_DoNotCorrupt_LastWriterWins()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _store.SaveStatusFieldsAsync("myorg", "Agile", $"content-{i}"));

        await Task.WhenAll(tasks);

        var result = await _store.LoadStatusFieldsAsync("myorg", "Agile");
        result.ShouldNotBeNull();
        result.ShouldStartWith("content-");
    }

    // ──────────────────────── Cancellation propagation ────────────────────────

    [Fact]
    public async Task LoadStatusFieldsAsync_PropagatesCancellation()
    {
        // Save a file first so the load path exercises ReadAllTextAsync
        await _store.SaveStatusFieldsAsync("myorg", "Agile", "content");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _store.LoadStatusFieldsAsync("myorg", "Agile", cts.Token));
    }

    [Fact]
    public async Task SaveStatusFieldsAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _store.SaveStatusFieldsAsync("myorg", "Agile", "content", cts.Token));

        // Verify no orphaned .tmp file was left behind
        var tmpPath = Path.Combine(_tempDir, "myorg", "Agile", "status-fields.tmp");
        File.Exists(tmpPath).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadMetadataAsync_PropagatesCancellation()
    {
        var metadata = new ProfileMetadata(
            "myorg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "sha256:abc", 1);
        await _store.SaveMetadataAsync("myorg", "Agile", metadata);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _store.LoadMetadataAsync("myorg", "Agile", cts.Token));
    }

    [Fact]
    public async Task SaveMetadataAsync_PropagatesCancellation()
    {
        var metadata = new ProfileMetadata(
            "myorg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "sha256:abc", 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _store.SaveMetadataAsync("myorg", "Agile", metadata, cts.Token));

        // Verify no orphaned .tmp file was left behind
        var tmpPath = Path.Combine(_tempDir, "myorg", "Agile", "profile.json.tmp");
        File.Exists(tmpPath).ShouldBeFalse();
    }
}
