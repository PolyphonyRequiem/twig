using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Domain.Interfaces;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ChangelogCommand"/>.
/// </summary>
public class ChangelogCommandTests
{
    private static readonly OutputFormatterFactory Formatter = new(
        new HumanOutputFormatter(), new JsonOutputFormatter(),
        new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

    // ── Formatting ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultipleReleases_FormatsEachRelease()
    {
        var releases = new List<GitHubReleaseInfo>
        {
            new("v2.0.0", "Release 2.0.0", "Bug fixes and improvements.", new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero), []),
            new("v1.1.0", "Release 1.1.0", "Added new feature X.", new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero), []),
            new("v1.0.0", "Release 1.0.0", "Initial release.", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), []),
        };

        var stub = new StubReleaseService(releases);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoReleases_PrintsMessage()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkError_Returns1()
    {
        var stub = new StubReleaseService(throwOnGet: new HttpRequestException("Network error"));
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_CountParameter_PassedToService()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        await command.ExecuteAsync(count: 10);

        stub.LastRequestedCount.ShouldBe(10);
    }

    [Fact]
    public async Task ExecuteAsync_ReleaseWithNoBody_PrintsNoReleaseNotes()
    {
        var releases = new List<GitHubReleaseInfo>
        {
            new("v1.0.0", "Release 1.0.0", "", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), []),
        };

        var stub = new StubReleaseService(releases);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_ReleaseWithNullDate_PrintsUnknownDate()
    {
        var releases = new List<GitHubReleaseInfo>
        {
            new("v1.0.0", "Release 1.0.0", "Notes here.", null, []),
        };

        var stub = new StubReleaseService(releases);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultCount_Is5()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        await command.ExecuteAsync();

        stub.LastRequestedCount.ShouldBe(5);
    }

    [Fact]
    public async Task ExecuteAsync_CountZero_ReturnsError()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync(count: 0);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_NegativeCount_ReturnsError()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        var exitCode = await command.ExecuteAsync(count: -5);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_CountAbove100_ClampsTo100()
    {
        var stub = new StubReleaseService([]);
        var command = new ChangelogCommand(stub, Formatter);

        await command.ExecuteAsync(count: 200);

        stub.LastRequestedCount.ShouldBe(100);
    }

    // ── Stub implementation────────────────────────────────────────────

    private sealed class StubReleaseService : IGitHubReleaseService
    {
        private readonly IReadOnlyList<GitHubReleaseInfo> _releases;
        private readonly Exception? _throwOnGet;

        public int LastRequestedCount { get; private set; }

        public StubReleaseService(IReadOnlyList<GitHubReleaseInfo>? releases = null, Exception? throwOnGet = null)
        {
            _releases = releases ?? [];
            _throwOnGet = throwOnGet;
        }

        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            return Task.FromResult(_releases.Count > 0 ? _releases[0] : (GitHubReleaseInfo?)null);
        }

        public Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(int count, CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            LastRequestedCount = count;
            return Task.FromResult(_releases);
        }

        public Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string tag, CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            return Task.FromResult(_releases.FirstOrDefault(r => r.Tag == tag));
        }
    }
}
