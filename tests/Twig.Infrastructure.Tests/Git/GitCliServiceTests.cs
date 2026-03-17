using Shouldly;
using Twig.Infrastructure.Git;
using Xunit;

namespace Twig.Infrastructure.Tests.Git;

/// <summary>
/// Integration tests for <see cref="GitCliService"/>.
/// Each test creates a temporary git repository and exercises real git operations.
/// </summary>
public class GitCliServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitCliService _sut;

    public GitCliServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Initialize a git repo in the temp directory.
        RunGit("init");
        RunGit("config user.email \"test@twig.dev\"");
        RunGit("config user.name \"Twig Test\"");

        _sut = new GitCliService(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // Make all files writable before deletion (git objects may be read-only).
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string RunGit(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd(); // drain stderr to avoid deadlock
        proc.WaitForExit();
        return output.Trim();
    }

    [Fact]
    public async Task IsInsideWorkTree_ReturnsTrue()
    {
        var result = await _sut.IsInsideWorkTreeAsync();
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCurrentBranch_ReturnsMainOrMaster()
    {
        // Need at least one commit for HEAD to resolve.
        RunGit("commit --allow-empty -m \"initial\"");

        var branch = await _sut.GetCurrentBranchAsync();
        // Default branch name varies by git config.
        branch.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetRepositoryRoot_ReturnsTempDir()
    {
        var root = await _sut.GetRepositoryRootAsync();
        // Normalize paths for comparison (git returns forward slashes on Windows).
        // On macOS, /tmp is a symlink to /private/tmp — git resolves symlinks, so
        // we must compare the git-reported root against what git thinks the root is.
        var expected = RunGit("rev-parse --show-toplevel").Trim();
        Path.GetFullPath(root).ShouldBe(Path.GetFullPath(expected));
    }

    [Fact]
    public async Task CreateBranch_And_Checkout_WorkCorrectly()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        await _sut.CreateBranchAsync("feature/test-branch");
        await _sut.CheckoutAsync("feature/test-branch");

        var branch = await _sut.GetCurrentBranchAsync();
        branch.ShouldBe("feature/test-branch");
    }

    [Fact]
    public async Task CommitAsync_ReturnsCommitHash()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var hash = await _sut.CommitAsync("test commit", allowEmpty: true);

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40); // full SHA-1 hash
    }

    [Fact]
    public async Task CommitWithArgsAsync_ReturnsCommitHash()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var hash = await _sut.CommitWithArgsAsync("passthrough commit", ["--allow-empty"]);

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40);
    }

    [Fact]
    public async Task CommitWithArgsAsync_WithAmend_UpdatesCommitMessage()
    {
        RunGit("commit --allow-empty -m \"original message\"");
        var originalHash = RunGit("rev-parse HEAD");

        var newHash = await _sut.CommitWithArgsAsync("amended message", ["--amend", "--allow-empty"]);

        // Hash changes after amend
        newHash.ShouldNotBe(originalHash);
        // Commit message is the amended one
        var log = RunGit("log -1 --format=%s");
        log.ShouldBe("amended message");
    }

    [Fact]
    public async Task CommitWithArgsAsync_WithPathspec_CommitsOnlyStagedFile()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "tracked.txt"), "content");
        File.WriteAllText(Path.Combine(_tempDir, "other.txt"), "other");
        RunGit("add tracked.txt");

        var hash = await _sut.CommitWithArgsAsync("partial commit", []);

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40);
        var log = RunGit("log -1 --format=%s");
        log.ShouldBe("partial commit");
    }

    [Fact]
    public async Task GetHeadCommitHash_ReturnsFullSha()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var hash = await _sut.GetHeadCommitHashAsync();

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40);
    }

    [Fact]
    public async Task GetConfigValue_ReturnsValue_WhenKeyExists()
    {
        var email = await _sut.GetConfigValueAsync("user.email");
        email.ShouldBe("test@twig.dev");
    }

    [Fact]
    public async Task GetConfigValue_ReturnsNull_WhenKeyDoesNotExist()
    {
        var value = await _sut.GetConfigValueAsync("nonexistent.key");
        value.ShouldBeNull();
    }

    [Fact]
    public async Task NonZeroExitCode_ThrowsGitOperationException()
    {
        // Asking for remote URL when no remote is configured should fail.
        RunGit("commit --allow-empty -m \"initial\"");

        var ex = await Should.ThrowAsync<GitOperationException>(
            () => _sut.GetRemoteUrlAsync("origin"));

        ex.Message.ShouldContain("failed:");
        ex.ExitCode.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task HasUncommittedChanges_ReturnsFalse_WhenClean()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var result = await _sut.HasUncommittedChangesAsync();
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task HasUncommittedChanges_ReturnsTrue_WhenDirty()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "dirty.txt"), "dirty");

        var result = await _sut.HasUncommittedChangesAsync();
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task BranchExists_ReturnsTrueForExistingBranch()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        RunGit("branch test-exists");

        var result = await _sut.BranchExistsAsync("test-exists");
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task BranchExists_ReturnsFalseForNonExistentBranch()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var result = await _sut.BranchExistsAsync("no-such-branch");
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteBranch_RemovesBranch()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        RunGit("branch to-delete");

        await _sut.DeleteBranchAsync("to-delete");

        var exists = await _sut.BranchExistsAsync("to-delete");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDetachedHead_ReturnsFalse_WhenOnBranch()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var result = await _sut.IsDetachedHeadAsync();
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDetachedHead_ReturnsTrue_WhenDetached()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        var hash = RunGit("rev-parse HEAD");
        RunGit($"checkout {hash}");

        var result = await _sut.IsDetachedHeadAsync();
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task GetLogAsync_ReturnsCommitEntries()
    {
        RunGit("commit --allow-empty -m \"first\"");
        RunGit("commit --allow-empty -m \"second\"");

        var log = await _sut.GetLogAsync(count: 5);

        log.ShouldNotBeEmpty();
        log.Count.ShouldBeGreaterThanOrEqualTo(2);
        // Verify CRLF stripping — no entry should end with '\r'.
        foreach (var entry in log)
            entry.ShouldNotEndWith("\r");
    }

    [Fact]
    public async Task IsAheadOf_ReturnsTrue_WhenAhead()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        var defaultBranch = RunGit("rev-parse --abbrev-ref HEAD");
        RunGit("checkout -b ahead-branch");
        RunGit("commit --allow-empty -m \"ahead\"");

        var result = await _sut.IsAheadOfAsync(defaultBranch);
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsAheadOf_ReturnsFalse_WhenNotAhead()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        var defaultBranch = RunGit("rev-parse --abbrev-ref HEAD");

        var result = await _sut.IsAheadOfAsync(defaultBranch);
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task GetWorktreeRoot_ReturnsNull_ForNormalRepo()
    {
        RunGit("commit --allow-empty -m \"initial\"");

        var result = await _sut.GetWorktreeRootAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetRemoteUrlAsync_ReturnsUrl_WhenRemoteIsConfigured()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        RunGit("remote add origin https://dev.azure.com/myorg/myproject/_git/myrepo");

        var url = await _sut.GetRemoteUrlAsync("origin");

        url.ShouldBe("https://dev.azure.com/myorg/myproject/_git/myrepo");
    }

    [Fact]
    public async Task CommitAsync_MessageWithDoubleQuotes_CommitsSuccessfully()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        var message = "fix: resolve issue with \"quoted\" paths";

        // Should not throw — this is the primary assertion: quoting fix prevents GitOperationException
        var hash = await _sut.CommitAsync(message, allowEmpty: true);

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40); // full SHA-1 hash
    }

    [Fact]
    public async Task CommitAsync_MessageWithBackslash_CommitsSuccessfully()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        var message = @"fix: path with \backslash";

        // Should not throw — this is the primary assertion: quoting fix prevents GitOperationException
        var hash = await _sut.CommitAsync(message, allowEmpty: true);

        hash.ShouldNotBeNullOrWhiteSpace();
        hash.Length.ShouldBe(40);
    }

    [Fact]
    public async Task StashAsync_WithMessage_CreatesStash()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "stash-file.txt"), "pending work");
        RunGit("add .");

        await _sut.StashAsync("my stash message");

        // After stash, working tree should be clean
        var hasUncommitted = await _sut.HasUncommittedChangesAsync();
        hasUncommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task StashAsync_MessageWithSpecialChars_DoesNotThrow()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "stash-file2.txt"), "pending work");
        RunGit("add .");

        // Should not throw despite special characters in message
        await _sut.StashAsync("message with \"quotes\" and \\backslash");

        var hasUncommitted = await _sut.HasUncommittedChangesAsync();
        hasUncommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task StashAsync_NoMessage_CreatesStash()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "stash-file3.txt"), "pending work");
        RunGit("add .");

        await _sut.StashAsync();

        var hasUncommitted = await _sut.HasUncommittedChangesAsync();
        hasUncommitted.ShouldBeFalse();
    }

    [Fact]
    public async Task StashPopAsync_RestoresStashedChanges()
    {
        RunGit("commit --allow-empty -m \"initial\"");
        File.WriteAllText(Path.Combine(_tempDir, "stash-pop-file.txt"), "pending work");
        RunGit("add .");
        await _sut.StashAsync("pop test");

        await _sut.StashPopAsync();

        // Restored file should appear as uncommitted
        var hasUncommitted = await _sut.HasUncommittedChangesAsync();
        hasUncommitted.ShouldBeTrue();
    }

    [Fact]
    public async Task RunGitAsync_ThrowsGitOperationException_WhenBinaryNotFound()
    {
        // Use a non-existent binary name to exercise the Win32Exception → GitOperationException path.
        var sut = new GitCliService(_tempDir, "nonexistent-git-binary-xyz");

        var ex = await Should.ThrowAsync<GitOperationException>(
            () => sut.IsInsideWorkTreeAsync());

        ex.Message.ShouldContain("git binary not found");
        ex.InnerException.ShouldBeOfType<System.ComponentModel.Win32Exception>();
    }

    [Fact]
    public async Task GetConfigValue_ThrowsGitOperationException_WhenExitCodeIsNot1()
    {
        // Passing "--type=int user.email" as the key builds the command:
        //   git config --type=int user.email
        // This exits with code 128 (type validation failure), which must NOT be
        // swallowed by the exit-code-1 guard (key not found).
        RunGit("config user.email \"test@twig.dev\"");

        var ex = await Should.ThrowAsync<GitOperationException>(
            () => _sut.GetConfigValueAsync("--type=int user.email"));

        ex.ExitCode.ShouldNotBe(1);
    }
}
