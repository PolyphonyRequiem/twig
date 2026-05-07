using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class TwigTokenFileCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cachePath;
    private readonly TwigTokenFileCache _cache;

    public TwigTokenFileCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-filecache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _cachePath = Path.Combine(_tempDir, ".token-cache");
        _cache = new TwigTokenFileCache(_cachePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNullToken()
    {
        var (token, expiry) = _cache.TryRead();
        token.ShouldBeNull();
        expiry.ShouldBe(default);
    }

    [Fact]
    public void TryWrite_ThenTryRead_RoundTripsTokenAndExpiry()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        const string token = "fake-token-xyz";

        _cache.TryWrite(token, expiry);
        var (read, readExpiry) = _cache.TryRead();

        read.ShouldBe(token);
        readExpiry.UtcTicks.ShouldBe(expiry.UtcTicks);
    }

    [Fact]
    public void TryWrite_OverwritesExistingFile()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        _cache.TryWrite("first", expiry);
        _cache.TryWrite("second", expiry);

        _cache.TryRead().Token.ShouldBe("second");
    }

    [Fact]
    public void TryDelete_RemovesFile()
    {
        _cache.TryWrite("to-be-deleted", DateTimeOffset.UtcNow.AddHours(1));
        File.Exists(_cachePath).ShouldBeTrue();

        _cache.TryDelete();

        File.Exists(_cachePath).ShouldBeFalse();
    }

    [Fact]
    public void TryDelete_MissingFile_DoesNotThrow()
    {
        Should.NotThrow(() => _cache.TryDelete());
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNullToken()
    {
        File.WriteAllText(_cachePath, "this is not a valid cache");

        var (token, expiry) = _cache.TryRead();
        token.ShouldBeNull();
        expiry.ShouldBe(default);
    }

    [Fact]
    public void TryRead_OnlyOneLine_ReturnsNullToken()
    {
        File.WriteAllText(_cachePath, "12345");

        var (token, _) = _cache.TryRead();
        token.ShouldBeNull();
    }

    [Fact]
    public void TryRead_EmptyTokenLine_ReturnsNullToken()
    {
        File.WriteAllText(_cachePath, $"{DateTimeOffset.UtcNow.AddHours(1).UtcTicks}\n\n");

        var (token, _) = _cache.TryRead();
        token.ShouldBeNull();
    }

    [Fact]
    public void TryWrite_CreatesDirectoryIfMissing()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "subdir", ".token-cache");
        var nestedCache = new TwigTokenFileCache(nestedPath);

        nestedCache.TryWrite("token", DateTimeOffset.UtcNow.AddHours(1));

        File.Exists(nestedPath).ShouldBeTrue();
    }

    [Fact]
    public void Path_ReturnsConfiguredPath()
    {
        _cache.Path.ShouldBe(_cachePath);
    }
}
