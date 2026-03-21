using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig hooks install</c> and <c>twig hooks uninstall</c>:
/// manages Twig-managed git hook scripts in the repository's <c>.git/hooks/</c> directory.
/// </summary>
public sealed class HooksCommand(
    HookInstaller hookInstaller,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null)
{
    /// <summary>Install Twig-managed git hooks.</summary>
    public async Task<int> InstallAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var gitDir = await ResolveGitDirAsync(fmt);
        if (gitDir is null)
            return 1;

        try
        {
            hookInstaller.Install(gitDir, config.Git.Hooks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to install hooks: {ex.Message}"));
            return 1;
        }

        Console.WriteLine(fmt.FormatSuccess("Twig hooks installed."));

        var hints = hintEngine.GetHints("hooks", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    /// <summary>Uninstall Twig-managed git hooks.</summary>
    public async Task<int> UninstallAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var gitDir = await ResolveGitDirAsync(fmt);
        if (gitDir is null)
            return 1;

        try
        {
            hookInstaller.Uninstall(gitDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to uninstall hooks: {ex.Message}"));
            return 1;
        }

        Console.WriteLine(fmt.FormatSuccess("Twig hooks uninstalled."));
        return 0;
    }

    private async Task<string?> ResolveGitDirAsync(IOutputFormatter fmt)
    {
        var (isValid, _) = await GitGuard.EnsureGitRepoAsync(gitService, fmt);
        if (!isValid)
            return null;

        try
        {
            var repoRoot = await gitService!.GetRepositoryRootAsync();
            return Path.Combine(repoRoot, ".git");
        }
        catch (Exception)
        {
            Console.Error.WriteLine(fmt.FormatError("Not inside a git repository."));
            return null;
        }
    }
}
