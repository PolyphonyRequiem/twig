using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class CurrentAttachmentLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-current-attachment-" + Guid.NewGuid().ToString("N"));
    private string TwigDirectory => Path.Combine(root, ".twig");

    [Fact]
    public async Task MissingAdmissionMarkerDoesNotRouteAnUnmarkedCache()
    {
        var cache = Path.Combine(TwigDirectory, "cache");
        Directory.CreateDirectory(cache);
        var unknown = Path.Combine(cache, "twig.db");
        await File.WriteAllTextAsync(unknown, "unadmitted original evidence");
        var resolved = MirrorAdmission.ResolveMirrorPath(TwigDirectory);
        resolved.ShouldNotBe(unknown);
        var paths = new TwigPaths(TwigDirectory, Path.Combine(TwigDirectory, "config"), resolved, root);
        Should.Throw<InvalidOperationException>(() => MirrorAdmission.Acquire(paths)).Message.ShouldContain("activation-required");
        (await File.ReadAllTextAsync(unknown)).ShouldBe("unadmitted original evidence");
        File.Exists(resolved).ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownLayoutIsRefusedWithoutAdoption()
    {
        await InitializeGit();
        var unknown = Path.Combine(TwigDirectory, "unknown");
        Directory.CreateDirectory(unknown);
        var evidence = Path.Combine(unknown, "original.db");
        await File.WriteAllTextAsync(evidence, "unrecognized original evidence");
        using var store = Store();
        var initialized = await store.InitializeAsync();
        initialized.IsSuccess.ShouldBeFalse();
        initialized.Error.Message.ShouldContain(AttachmentStorageFailure.UnsupportedLayout);
        (await File.ReadAllTextAsync(evidence)).ShouldBe("unrecognized original evidence");
        File.Exists(Path.Combine(TwigDirectory, "layout.json")).ShouldBeFalse();
        File.Exists(Path.Combine(TwigDirectory, "attachment.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExistingUnsupportedMarkerAndFingerprintAreNeverOverwritten()
    {
        await InitializeGit();
        using var store = Store();
        (await store.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var attachment = Path.Combine(TwigDirectory, "attachment.json");
        var before = await File.ReadAllTextAsync(attachment);
        var fingerprint = Path.Combine(TwigDirectory, "worktree.json");
        await File.WriteAllTextAsync(fingerprint, """{"schema":"unsupported","version":999}""");
        (await store.InitializeAsync()).IsSuccess.ShouldBeFalse();
        (await File.ReadAllTextAsync(fingerprint)).ShouldBe("""{"schema":"unsupported","version":999}""");
        (await File.ReadAllTextAsync(attachment)).ShouldBe(before);
    }

    [Fact]
    public async Task ConcurrentInitializationReopensTheSameCurrentAttachment()
    {
        await InitializeGit();
        using var first = Store();
        using var second = Store();
        var initialized = await Task.WhenAll(Task.Run(() => first.InitializeAsync()), Task.Run(() => second.InitializeAsync()));
        initialized.ShouldAllBe(result => result.IsSuccess);
        var before = await File.ReadAllTextAsync(Path.Combine(TwigDirectory, "attachment.json"));
        (await first.InitializeAsync()).IsSuccess.ShouldBeTrue();
        (await File.ReadAllTextAsync(Path.Combine(TwigDirectory, "attachment.json"))).ShouldBe(before);
        (await second.ReadWithRevisionAsync()).Value.Revision.ShouldBe(0);
    }

    private WorktreeLocalAttachmentStore Store() => new(
        new TwigPaths(TwigDirectory, Path.Combine(TwigDirectory, "config"), Path.Combine(TwigDirectory, "cache", "mirror.db"), root),
        new TwigConfiguration { Organization = "org", Project = "project" }, TimeProvider.System);

    private async Task InitializeGit()
    {
        Directory.CreateDirectory(root);
        using var git = Process.Start(new ProcessStartInfo("git", "init --quiet")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        git.ShouldNotBeNull();
        git.StandardInput.Close();
        await git.WaitForExitAsync();
        git.ExitCode.ShouldBe(0, await git.StandardError.ReadToEndAsync());
        WorktreeAnchorDetector.TryDetect(root, out _, out _).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
