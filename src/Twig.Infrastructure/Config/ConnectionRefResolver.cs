using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Computes <c>connectionRef</c> per AB#736 §5.1:
/// <c>lowercase-hex(sha256(canonical-json({ "organization": &lt;org&gt;,
/// "project": &lt;project&gt; })))</c>. Team is intentionally excluded so a team
/// change never invalidates registry rows. Canonical JSON is UTF-8, sorted keys,
/// no whitespace.
/// </summary>
internal static class ConnectionRefResolver
{
    public static string Compute(string organization, string project)
    {
        // Normalize the organization so a slug and a full URI collapse to the
        // same canonical form. Two contributors — one who checked in
        // "contoso" and one who checked in "https://dev.azure.com/contoso" —
        // MUST agree on the same registry row, or the T1 system store treats
        // them as distinct worktrees.
        var orgSlug = OrganizationNormalizer.ToSlug(organization ?? string.Empty);
        // Canonical JSON with sorted keys and no whitespace, per §5.1. We use a
        // deliberate hand-rolled writer here — the source-generated context emits
        // whitespace only when explicitly asked, but "sorted keys" is not a
        // guarantee it makes, so any change to member order would silently drift
        // the ref. This is the sole call site that treats those bytes as a hash
        // input; the whole rest of Twig serializes freely.
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["organization"] = orgSlug,
            ["project"] = project ?? string.Empty,
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            foreach (var (k, v) in payload)
                writer.WriteString(k, v);
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.ToArray());
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Convenience overload: read from a <see cref="TwigConfiguration"/>.</summary>
    public static string Compute(TwigConfiguration config) =>
        Compute(config.Organization, config.Project);

    // Suppress: the JSON context reference keeps the tree-shaker from stripping the
    // canonicaliser above when only ConnectionRefResolver is invoked from init.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0051")]
    private static void _EnsureJsonContextReferenced() => _ = TwigJsonContext.Default;
}
