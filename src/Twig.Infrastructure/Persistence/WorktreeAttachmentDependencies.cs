using System.Text.Json;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Infrastructure adapter for
/// <see cref="Twig.Domain.Services.Attachment.IWorktreeFingerprintProvider"/>.
/// Resolves the §3.2 anchor tuple from a live <c>git rev-parse</c> and canonicalizes
/// it per AB#736 §5.2 (UTF-8, sorted keys, no whitespace) so equality against
/// <c>system.db.worktrees.worktreeFingerprint</c> is byte-equal on the canonical
/// form. Failures are surfaced as an empty fingerprint tuple whose downstream
/// checks refuse — the storage layer owns the fail-closed identifier, so the
/// provider stays passive.
/// </summary>
internal sealed class WorktreeFingerprintProvider : IWorktreeFingerprintProvider
{
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;

    public WorktreeFingerprintProvider(TwigPaths paths, TwigConfiguration config)
    {
        _paths = paths;
        _config = config;
    }

    public WorktreeFingerprintContext CurrentFingerprint
    {
        get
        {
            var startDir = _paths.StartDir ?? _paths.TwigDir;
            if (!WorktreeAnchorDetector.TryDetect(startDir, out var anchor, out _))
                return new WorktreeFingerprintContext(string.Empty, string.Empty, string.Empty);

            var canonical = CanonicalJson(anchor);
            var connectionRef = ConnectionRefResolver.Compute(_config);
            return new WorktreeFingerprintContext(canonical, connectionRef, anchor.WorktreeRoot);
        }
    }

    internal static string CanonicalJson(WorktreeAnchor anchor)
    {
        // AB#736 §5.2 — sorted keys, no whitespace, UTF-8.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("gitCommonDir", anchor.GitCommonDir);
            writer.WriteString("worktreeGitDir", anchor.WorktreeGitDir);
            writer.WriteString("worktreeRoot", anchor.WorktreeRoot);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Infrastructure adapter for
/// <see cref="Twig.Domain.Services.Attachment.IPrimaryScopeUrlBuilder"/>.
/// Delegates to <see cref="AdoWorkItemUrlValidator.BuildWorkItemUrl"/> so the
/// URL a write emits is the exact shape the read path validates against.
/// </summary>
internal sealed class ConfiguredPrimaryScopeUrlBuilder : IPrimaryScopeUrlBuilder
{
    private readonly TwigConfiguration _config;

    public ConfiguredPrimaryScopeUrlBuilder(TwigConfiguration config)
    {
        _config = config;
    }

    public string BuildWorkItemUrl(int workItemId) =>
        AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization ?? string.Empty, _config.Project ?? string.Empty, workItemId);
}
