using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="TwigRefreshTokenStore"/>: round-trip serialization, missing/corrupt
/// file handling, and atomic write semantics. The store is best-effort by design — every
/// failure mode must surface as a null/false rather than an exception.
/// </summary>
public sealed class TwigRefreshTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly TwigRefreshTokenStore _store;

    public TwigRefreshTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-refresh-store-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, ".refresh-token");
        _store = new TwigRefreshTokenStore(_path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Path_ReflectsConstructorArgument()
    {
        _store.Path.ShouldBe(_path);
    }

    [Fact]
    public void Exists_FileMissing_ReturnsFalse()
    {
        _store.Exists().ShouldBeFalse();
    }

    [Fact]
    public void TryRead_FileMissing_ReturnsNull()
    {
        _store.TryRead().ShouldBeNull();
    }

    [Fact]
    public void TryWriteAndRead_RoundTrip_PreservesAllFields()
    {
        var entry = new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "rt-secret-abc",
            ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
            TenantId = "tenant-1234",
            AuthorityHost = "login.microsoftonline.com",
            UserPrincipalName = "user@contoso.com",
            ObjectId = "oid-9876",
            BootstrappedAt = "2025-01-15T10:30:00Z",
            Source = "azcli",
        };

        _store.TryWrite(entry);

        _store.Exists().ShouldBeTrue();
        var roundTripped = _store.TryRead().ShouldNotBeNull();
        roundTripped.RefreshToken.ShouldBe(entry.RefreshToken);
        roundTripped.ClientId.ShouldBe(entry.ClientId);
        roundTripped.TenantId.ShouldBe(entry.TenantId);
        roundTripped.AuthorityHost.ShouldBe(entry.AuthorityHost);
        roundTripped.UserPrincipalName.ShouldBe(entry.UserPrincipalName);
        roundTripped.ObjectId.ShouldBe(entry.ObjectId);
        roundTripped.BootstrappedAt.ShouldBe(entry.BootstrappedAt);
        roundTripped.Source.ShouldBe(entry.Source);
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNullDoesNotThrow()
    {
        File.WriteAllText(_path, "{ not valid json at all ::: ");

        Should.NotThrow(() => _store.TryRead().ShouldBeNull());
    }

    [Fact]
    public void TryRead_EmptyFile_ReturnsNullDoesNotThrow()
    {
        File.WriteAllText(_path, "");

        Should.NotThrow(() => _store.TryRead().ShouldBeNull());
    }

    [Fact]
    public void TryDelete_RemovesFile()
    {
        _store.TryWrite(new TwigRefreshTokenStoreEntry { RefreshToken = "x" });
        _store.Exists().ShouldBeTrue();

        _store.TryDelete();

        _store.Exists().ShouldBeFalse();
    }

    [Fact]
    public void TryDelete_FileMissing_DoesNotThrow()
    {
        Should.NotThrow(() => _store.TryDelete());
    }

    [Fact]
    public void TryWrite_CreatesParentDirectoryIfMissing()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "subdir", ".refresh-token");
        var store = new TwigRefreshTokenStore(nestedPath);

        store.TryWrite(new TwigRefreshTokenStoreEntry { RefreshToken = "x" });

        File.Exists(nestedPath).ShouldBeTrue();
    }

    [Fact]
    public void TryWrite_OverwritesExisting()
    {
        _store.TryWrite(new TwigRefreshTokenStoreEntry { RefreshToken = "first" });
        _store.TryWrite(new TwigRefreshTokenStoreEntry { RefreshToken = "second" });

        var entry = _store.TryRead().ShouldNotBeNull();
        entry.RefreshToken.ShouldBe("second");
    }

    [Fact]
    public void TryWrite_WritesAtomically_NoTmpFileLeftBehind()
    {
        _store.TryWrite(new TwigRefreshTokenStoreEntry { RefreshToken = "x" });

        File.Exists(_path + ".tmp").ShouldBeFalse();
    }
}
