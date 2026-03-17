using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Xunit;

namespace Twig.Infrastructure.Tests.Git;

public class HookInstallerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _gitDir;
    private readonly string _hooksDir;
    private readonly HookInstaller _installer;

    public HookInstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-hooks-test-{Guid.NewGuid():N}");
        _gitDir = Path.Combine(_tempDir, ".git");
        _hooksDir = Path.Combine(_gitDir, "hooks");
        Directory.CreateDirectory(_gitDir);
        _installer = new HookInstaller();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Install creates hooks directory ─────────────────────────────

    [Fact]
    public void Install_CreatesHooksDirectory()
    {
        _installer.Install(_gitDir, new HooksConfig());

        Directory.Exists(_hooksDir).ShouldBeTrue();
    }

    // ── Install writes all three hook files ─────────────────────────

    [Fact]
    public void Install_WritesAllThreeHookFiles()
    {
        _installer.Install(_gitDir, new HooksConfig());

        File.Exists(Path.Combine(_hooksDir, "prepare-commit-msg")).ShouldBeTrue();
        File.Exists(Path.Combine(_hooksDir, "commit-msg")).ShouldBeTrue();
        File.Exists(Path.Combine(_hooksDir, "post-checkout")).ShouldBeTrue();
    }

    // ── Hook files contain marker comments ──────────────────────────

    [Fact]
    public void Install_HookFilesContainMarkerComments()
    {
        _installer.Install(_gitDir, new HooksConfig());

        foreach (var hookName in new[] { "prepare-commit-msg", "commit-msg", "post-checkout" })
        {
            var content = File.ReadAllText(Path.Combine(_hooksDir, hookName));
            content.ShouldContain(HookInstaller.MarkerStart);
            content.ShouldContain(HookInstaller.MarkerEnd);
        }
    }

    // ── Hook files start with shebang ───────────────────────────────

    [Fact]
    public void Install_HookFilesStartWithShebang()
    {
        _installer.Install(_gitDir, new HooksConfig());

        foreach (var hookName in new[] { "prepare-commit-msg", "commit-msg", "post-checkout" })
        {
            var content = File.ReadAllText(Path.Combine(_hooksDir, hookName));
            content.ShouldStartWith("#!/bin/sh");
        }
    }

    // ── Hook scripts invoke twig _hook ──────────────────────────────

    [Fact]
    public void Install_HookScriptsInvokeTwigHook()
    {
        _installer.Install(_gitDir, new HooksConfig());

        var prepareCommitMsg = File.ReadAllText(Path.Combine(_hooksDir, "prepare-commit-msg"));
        prepareCommitMsg.ShouldContain("twig _hook prepare-commit-msg");

        var commitMsg = File.ReadAllText(Path.Combine(_hooksDir, "commit-msg"));
        commitMsg.ShouldContain("twig _hook commit-msg");

        var postCheckout = File.ReadAllText(Path.Combine(_hooksDir, "post-checkout"));
        postCheckout.ShouldContain("twig _hook post-checkout");
    }

    // ── Config controls which hooks are installed ───────────────────

    [Fact]
    public void Install_RespectsHooksConfig()
    {
        var config = new HooksConfig
        {
            PrepareCommitMsg = false,
            CommitMsg = true,
            PostCheckout = false,
        };

        _installer.Install(_gitDir, config);

        File.Exists(Path.Combine(_hooksDir, "prepare-commit-msg")).ShouldBeFalse();
        File.Exists(Path.Combine(_hooksDir, "commit-msg")).ShouldBeTrue();
        File.Exists(Path.Combine(_hooksDir, "post-checkout")).ShouldBeFalse();
    }

    // ── Preserves existing hook content ─────────────────────────────

    [Fact]
    public void Install_PreservesExistingHookContent()
    {
        Directory.CreateDirectory(_hooksDir);
        var hookPath = Path.Combine(_hooksDir, "prepare-commit-msg");
        File.WriteAllText(hookPath, "#!/bin/sh\necho 'existing hook'\n");

        _installer.Install(_gitDir, new HooksConfig());

        var content = File.ReadAllText(hookPath);
        content.ShouldContain("echo 'existing hook'");
        content.ShouldContain(HookInstaller.MarkerStart);
    }

    // ── Re-install replaces Twig section without duplicating ────────

    [Fact]
    public void Install_ReinstallDoesNotDuplicate()
    {
        _installer.Install(_gitDir, new HooksConfig());
        _installer.Install(_gitDir, new HooksConfig());

        var content = File.ReadAllText(Path.Combine(_hooksDir, "post-checkout"));
        var startCount = CountOccurrences(content, HookInstaller.MarkerStart);
        startCount.ShouldBe(1);
    }

    // ── Uninstall removes Twig sections ─────────────────────────────

    [Fact]
    public void Uninstall_RemovesTwigSections()
    {
        _installer.Install(_gitDir, new HooksConfig());

        _installer.Uninstall(_gitDir);

        // Files should be deleted since only Twig content + shebang
        File.Exists(Path.Combine(_hooksDir, "prepare-commit-msg")).ShouldBeFalse();
        File.Exists(Path.Combine(_hooksDir, "commit-msg")).ShouldBeFalse();
        File.Exists(Path.Combine(_hooksDir, "post-checkout")).ShouldBeFalse();
    }

    // ── Uninstall preserves non-Twig content ────────────────────────

    [Fact]
    public void Uninstall_PreservesExistingContent()
    {
        Directory.CreateDirectory(_hooksDir);
        var hookPath = Path.Combine(_hooksDir, "prepare-commit-msg");
        File.WriteAllText(hookPath, "#!/bin/sh\necho 'user hook'\n");

        _installer.Install(_gitDir, new HooksConfig());
        _installer.Uninstall(_gitDir);

        File.Exists(hookPath).ShouldBeTrue();
        var content = File.ReadAllText(hookPath);
        content.ShouldContain("echo 'user hook'");
        content.ShouldNotContain(HookInstaller.MarkerStart);
        content.ShouldNotContain(HookInstaller.MarkerEnd);
    }

    // ── Uninstall on empty directory is a no-op ─────────────────────

    [Fact]
    public void Uninstall_NoHooksDirectory_IsNoOp()
    {
        // No hooks directory exists — should not throw
        _installer.Uninstall(_gitDir);
    }

    // ── Uninstall when no hook files exist is a no-op ───────────────

    [Fact]
    public void Uninstall_NoHookFiles_IsNoOp()
    {
        Directory.CreateDirectory(_hooksDir);
        _installer.Uninstall(_gitDir);
    }

    // ── StripTwigSection removes section correctly ──────────────────

    [Fact]
    public void StripTwigSection_RemovesOnlyTwigPart()
    {
        var input = $"#!/bin/sh\necho 'before'\n{HookInstaller.MarkerStart}\ntwig _hook post-checkout\n{HookInstaller.MarkerEnd}\necho 'after'\n";
        var result = HookInstaller.StripTwigSection(input);

        result.ShouldContain("echo 'before'");
        result.ShouldContain("echo 'after'");
        result.ShouldNotContain(HookInstaller.MarkerStart);
        result.ShouldNotContain("twig _hook");
    }

    // ── StripTwigSection with no markers returns unchanged ──────────

    [Fact]
    public void StripTwigSection_NoMarkers_ReturnsUnchanged()
    {
        var input = "#!/bin/sh\necho 'hello'\n";
        var result = HookInstaller.StripTwigSection(input);
        result.ShouldBe(input);
    }

    // ── GenerateHookScript produces valid content ───────────────────

    [Theory]
    [InlineData("prepare-commit-msg", "twig _hook prepare-commit-msg")]
    [InlineData("commit-msg", "twig _hook commit-msg")]
    [InlineData("post-checkout", "twig _hook post-checkout")]
    public void GenerateHookScript_ContainsExpectedCommand(string hookName, string expectedCommand)
    {
        var script = HookInstaller.GenerateHookScript(hookName);
        script.ShouldContain(expectedCommand);
        script.ShouldContain(HookInstaller.MarkerStart);
        script.ShouldContain(HookInstaller.MarkerEnd);
    }

    // ── Existing hook without shebang gets one added ────────────────

    [Fact]
    public void Install_ExistingHookWithoutShebang_AddsShebang()
    {
        Directory.CreateDirectory(_hooksDir);
        var hookPath = Path.Combine(_hooksDir, "commit-msg");
        File.WriteAllText(hookPath, "echo 'no shebang'\n");

        _installer.Install(_gitDir, new HooksConfig());

        var content = File.ReadAllText(hookPath);
        content.ShouldStartWith("#!/bin/sh");
        content.ShouldContain("echo 'no shebang'");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
