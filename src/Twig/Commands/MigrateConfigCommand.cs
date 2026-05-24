using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// AB#3296: implements <c>twig migrate-config</c>. Splits the legacy single-file
/// <c>.twig/config</c> into a committed <c>twig.json</c> manifest at the repo root
/// (repo coordinates) plus a gitignored <c>.twig/config</c> (per-user preferences).
/// Idempotent — re-running converges on the correct post-conditions:
/// <list type="bullet">
/// <item><c>twig.json</c> at repo root contains only <see cref="TwigRepoConfig"/> shape</item>
/// <item><c>.twig/config</c> contains only <see cref="TwigUserConfig"/> shape</item>
/// <item><c>.gitignore</c> at repo root ignores <c>.twig/</c> and removes any
///   stale <c>!.twig/config</c> negation that would leak user prefs into commits</item>
/// </list>
/// Never auto-runs from <c>twig sync</c> — must be invoked explicitly so polyphony
/// worktrees and other un-migrated repos are never re-dirtied behind the user's back.
/// </summary>
public sealed class MigrateConfigCommand(TwigPaths paths, OutputFormatterFactory formatterFactory)
{
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var hasLegacyConfig = File.Exists(paths.ConfigPath);
        var hasRepoManifest = File.Exists(paths.RepoConfigPath);

        if (!hasLegacyConfig && !hasRepoManifest)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"No twig configuration found at '{paths.RepoRoot}'. Run 'twig init' first."));
            return 1;
        }

        // Load whatever exists. LoadSplitAsync handles all three cases:
        //   legacy-only, manifest-only, or both-already-split.
        var config = await TwigConfiguration.LoadSplitAsync(paths, ct);

        var didWork = false;
        var changes = new List<string>();

        // Always write the manifest (split shape). The byte-identity short-circuit
        // makes this a no-op if twig.json is already in canonical form.
        if (dryRun)
        {
            changes.Add($"  would write {paths.RepoConfigPath}");
        }
        else
        {
            var before = File.Exists(paths.RepoConfigPath) ? File.ReadAllBytes(paths.RepoConfigPath) : null;
            await config.SaveRepoAsync(paths.RepoConfigPath, ct);
            var after = File.ReadAllBytes(paths.RepoConfigPath);
            if (before is null || !before.AsSpan().SequenceEqual(after))
            {
                changes.Add(hasRepoManifest
                    ? $"  updated {RelativeTo(paths.RepoConfigPath, paths.RepoRoot)}"
                    : $"  created {RelativeTo(paths.RepoConfigPath, paths.RepoRoot)}");
                didWork = true;
            }
        }

        // Rewrite the user-prefs file with only TwigUserConfig content. In legacy
        // mode this strips repo coords from the file (the source of the leak).
        if (dryRun)
        {
            changes.Add($"  would rewrite {paths.ConfigPath} as user-prefs-only");
        }
        else
        {
            var before = File.Exists(paths.ConfigPath) ? File.ReadAllBytes(paths.ConfigPath) : null;
            await config.SaveUserAsync(paths.ConfigPath, ct);
            var after = File.Exists(paths.ConfigPath) ? File.ReadAllBytes(paths.ConfigPath) : null;
            if (after is not null && (before is null || !before.AsSpan().SequenceEqual(after.AsSpan())))
            {
                changes.Add(hasLegacyConfig
                    ? $"  rewrote {RelativeTo(paths.ConfigPath, paths.RepoRoot)} as user-prefs-only"
                    : $"  created {RelativeTo(paths.ConfigPath, paths.RepoRoot)}");
                didWork = true;
            }
        }

        // Fix .gitignore at repo root.
        var gitignorePath = Path.Combine(paths.RepoRoot, ".gitignore");
        var gitignoreReport = UpdateGitignore(gitignorePath, dryRun);
        if (gitignoreReport.Changed)
        {
            changes.Add(dryRun
                ? $"  would update {RelativeTo(gitignorePath, paths.RepoRoot)}: {gitignoreReport.Summary}"
                : $"  updated {RelativeTo(gitignorePath, paths.RepoRoot)}: {gitignoreReport.Summary}");
            didWork = true;
        }

        if (changes.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("Configuration is already in the split shape — nothing to do."));
            return 0;
        }

        if (dryRun)
        {
            Console.WriteLine(fmt.FormatInfo("Dry run — no files were modified:"));
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess(didWork
                ? "Migrated configuration to the split shape (AB#3296)."
                : "Configuration already in split shape — no rewrites needed."));
        }

        foreach (var change in changes)
            Console.WriteLine(fmt.FormatInfo(change));

        if (!dryRun)
        {
            Console.WriteLine();
            Console.WriteLine(fmt.FormatInfo("Next steps:"));
            Console.WriteLine(fmt.FormatInfo($"  git add {RelativeTo(paths.RepoConfigPath, paths.RepoRoot)} .gitignore"));
            Console.WriteLine(fmt.FormatInfo("  git rm --cached .twig/config   # if the legacy file was previously tracked"));
            Console.WriteLine(fmt.FormatInfo("  git commit -m \"chore(twig): adopt twig.json split (AB#3296)\""));
        }

        return 0;
    }

    private static string RelativeTo(string fullPath, string root)
    {
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static (bool Changed, string Summary) UpdateGitignore(string gitignorePath, bool dryRun)
    {
        // Postconditions:
        //   - ".twig/" appears as an ignore pattern at least once.
        //   - No "!.twig/config" or "!.twig/repo.json" negation patterns survive
        //     (those used to leak user prefs back into committed history).
        const string ignorePattern = ".twig/";
        var changes = new List<string>();

        var lines = File.Exists(gitignorePath)
            ? File.ReadAllLines(gitignorePath).ToList()
            : new List<string>();

        // Strip negations of anything under .twig/.
        var beforeCount = lines.Count;
        var negationsRemoved = lines.RemoveAll(l =>
        {
            var trimmed = l.Trim();
            return trimmed.StartsWith("!.twig/", StringComparison.Ordinal);
        });
        if (negationsRemoved > 0)
            changes.Add($"removed {negationsRemoved} stale '!.twig/...' negation(s)");

        var hasIgnore = lines.Any(l => l.Trim() == ignorePattern || l.Trim() == ignorePattern.TrimEnd('/'));
        if (!hasIgnore)
        {
            lines.Add(ignorePattern);
            changes.Add($"added '{ignorePattern}'");
        }

        if (changes.Count == 0)
            return (false, "no changes");

        if (!dryRun)
        {
            File.WriteAllLines(gitignorePath, lines);
        }

        return (true, string.Join(", ", changes));
    }
}
