using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class HooksCommandTests
{
    private readonly HookInstaller _hookInstaller;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;

    public HooksCommandTests()
    {
        _hookInstaller = new HookInstaller();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
        _gitService = Substitute.For<IGitService>();
    }

    private HooksCommand CreateCommand(IGitService? gitService = null) =>
        new(_hookInstaller, _formatterFactory, _hintEngine, _config, gitService: gitService);

    // ── Install: no git service ─────────────────────────────────────

    [Fact]
    public async Task Install_NoGitService_ReturnsError()
    {
        var cmd = CreateCommand(gitService: null);
        var result = await cmd.InstallAsync();
        result.ShouldBe(1);
    }

    // ── Install: not in work tree ───────────────────────────────────

    [Fact]
    public async Task Install_NotInWorkTree_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.InstallAsync();
        result.ShouldBe(1);
    }

    // ── Install: git exception ──────────────────────────────────────

    [Fact]
    public async Task Install_GitThrows_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitOperationException("not a repo"));

        var cmd = CreateCommand(_gitService);
        var result = await cmd.InstallAsync();
        result.ShouldBe(1);
    }

    // ── Install: success with temp directory ────────────────────────

    [Fact]
    public async Task Install_Success_ReturnsZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-hooks-cmd-{Guid.NewGuid():N}");
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);

        try
        {
            // Use forward slashes in the repo root since git returns unix-style paths
            var repoRoot = tempDir.Replace('\\', '/');
            _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
            _gitService.GetRepositoryRootAsync(Arg.Any<CancellationToken>()).Returns(repoRoot);

            var cmd = CreateCommand(_gitService);

            var sw = new StringWriter();
            var original = Console.Out;
            Console.SetOut(sw);
            try
            {
                var result = await cmd.InstallAsync();
                result.ShouldBe(0);
                sw.ToString().ShouldContain("installed");
            }
            finally
            {
                Console.SetOut(original);
            }

            // Verify hook files exist
            var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
            File.Exists(Path.Combine(hooksDir, "post-checkout")).ShouldBeTrue();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Uninstall: no git service ───────────────────────────────────

    [Fact]
    public async Task Uninstall_NoGitService_ReturnsError()
    {
        var cmd = CreateCommand(gitService: null);
        var result = await cmd.UninstallAsync();
        result.ShouldBe(1);
    }

    // ── Uninstall: not in work tree ─────────────────────────────────

    [Fact]
    public async Task Uninstall_NotInWorkTree_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.UninstallAsync();
        result.ShouldBe(1);
    }

    // ── Uninstall: success ──────────────────────────────────────────

    [Fact]
    public async Task Uninstall_Success_ReturnsZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-hooks-cmd-{Guid.NewGuid():N}");
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);

        try
        {
            var repoRoot = tempDir.Replace('\\', '/');
            _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
            _gitService.GetRepositoryRootAsync(Arg.Any<CancellationToken>()).Returns(repoRoot);

            // Install first, then uninstall
            _hookInstaller.Install(Path.Combine(repoRoot, ".git"), new HooksConfig());

            var cmd = CreateCommand(_gitService);

            var sw = new StringWriter();
            var original = Console.Out;
            Console.SetOut(sw);
            try
            {
                var result = await cmd.UninstallAsync();
                result.ShouldBe(0);
                sw.ToString().ShouldContain("uninstalled");
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
