using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

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
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// machine formats emit a single "configMigrated" / "configMigrationNoop" document with
/// <c>changes</c> and (when applicable) <c>nextSteps</c> arrays. Human format streams
/// the legacy multi-line layout via individual <see cref="RenderNode.Text"/> and
/// <see cref="RenderNode.Hint"/> nodes.
/// </remarks>
public sealed class MigrateConfigCommand(
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

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

        var config = await TwigConfiguration.LoadSplitAsync(paths, ct);

        var didWork = false;
        var changes = new List<string>();

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

        var gitignorePath = Path.Combine(paths.RepoRoot, ".gitignore");
        var gitignoreReport = UpdateGitignore(gitignorePath, dryRun);
        if (gitignoreReport.Changed)
        {
            changes.Add(dryRun
                ? $"  would update {RelativeTo(gitignorePath, paths.RepoRoot)}: {gitignoreReport.Summary}"
                : $"  updated {RelativeTo(gitignorePath, paths.RepoRoot)}: {gitignoreReport.Summary}");
            didWork = true;
        }

        var nextSteps = (!dryRun && changes.Count > 0)
            ? new List<string>
            {
                $"  git add {RelativeTo(paths.RepoConfigPath, paths.RepoRoot)} .gitignore",
                "  git rm --cached .twig/config   # if the legacy file was previously tracked",
                "  git commit -m \"chore(twig): adopt twig.json split (AB#3296)\"",
            }
            : new List<string>();

        var tree = BuildTree(dryRun, didWork, changes, nextSteps, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    private static RenderTree.RenderTree BuildTree(
        bool dryRun, bool didWork, List<string> changes, List<string> nextSteps, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";

        if (changes.Count == 0)
        {
            const string noopMessage = "Configuration is already in the split shape — nothing to do.";
            RenderNode noopNode = lower switch
            {
                "minimal" => new RenderNode.Text(noopMessage),
                "json" or "json-full" or "json-compact" or "ids" =>
                    new RenderNode.Record("configMigrationNoop", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["message"] = RenderCell.String(noopMessage),
                    }),
                _ => new RenderNode.Text(noopMessage, Severity.Info),
            };
            return new RenderTree.RenderTree(new[] { noopNode });
        }

        if (isMachine && lower != "minimal")
        {
            var fields = new List<DocumentField>(4)
            {
                new("dryRun", new RenderNode.KeyValue("dryRun", RenderCell.Boolean(dryRun))),
                new("didWork", new RenderNode.KeyValue("didWork", RenderCell.Boolean(didWork))),
                new("changes", BuildMessageTable("change", changes)),
            };
            if (nextSteps.Count > 0)
                fields.Add(new("nextSteps", BuildMessageTable("nextStep", nextSteps)));
            var doc = new RenderNode.Document("configMigrated", fields);
            return new RenderTree.RenderTree(new[] { (RenderNode)doc });
        }

        // Human (and minimal) format — stream line by line.
        var summary = dryRun
            ? "Dry run — no files were modified:"
            : (didWork
                ? "Migrated configuration to the split shape (AB#3296)."
                : "Configuration already in split shape — no rewrites needed.");
        var humanNodes = new List<RenderNode>(1 + changes.Count + (nextSteps.Count > 0 ? nextSteps.Count + 2 : 0));
        humanNodes.Add(lower == "minimal"
            ? new RenderNode.Text(summary)
            : new RenderNode.Text(summary, dryRun ? Severity.Info : Severity.Success));

        foreach (var change in changes)
            humanNodes.Add(new RenderNode.Text(change, Severity.Info));

        if (lower != "minimal" && nextSteps.Count > 0)
        {
            humanNodes.Add(new RenderNode.Text(string.Empty));
            humanNodes.Add(new RenderNode.Text("Next steps:", Severity.Info));
            foreach (var step in nextSteps)
                humanNodes.Add(new RenderNode.Hint(step));
        }

        return new RenderTree.RenderTree(humanNodes);
    }

    private static RenderNode.Table BuildMessageTable(string kind, List<string> messages)
    {
        var columns = new List<RenderColumn> { new("message", "Message") };
        var rows = new List<RenderRow>(messages.Count);
        foreach (var message in messages)
        {
            var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["message"] = RenderCell.String(message),
            };
            rows.Add(new RenderRow(kind, cells));
        }
        return new RenderNode.Table(null, columns, rows);
    }

    private static string RelativeTo(string fullPath, string root)
    {
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static (bool Changed, string Summary) UpdateGitignore(string gitignorePath, bool dryRun)
    {
        const string ignorePattern = ".twig/";
        var changes = new List<string>();

        var lines = File.Exists(gitignorePath)
            ? File.ReadAllLines(gitignorePath).ToList()
            : new List<string>();

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
