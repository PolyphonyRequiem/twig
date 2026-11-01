using System.Text.Json;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Persistence;

internal static class WorktreeFingerprintProvider
{
    internal static string CanonicalJson(WorktreeAnchor anchor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
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
