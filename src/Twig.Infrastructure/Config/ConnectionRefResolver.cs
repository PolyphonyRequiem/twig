using System.Security.Cryptography;
using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Computes <c>connectionRef</c> per AB#736 §5.1:
/// <c>lowercase-hex(sha256(canonical-json({ "organization": &lt;org&gt;,
/// "project": &lt;project&gt; })))</c>. Both values are hashed <b>opaque and
/// verbatim</b> — the T1 spec fixes them as strings, not URI-normalized
/// slugs. Normalization (slug versus full URI, casing) is a
/// <b>presentation</b> concern owned by <see cref="OrganizationNormalizer"/>
/// for URL construction and origin validation only; hashing the normalized
/// form would silently drift the <c>connectionRef</c> across every
/// contributor who has an older <c>twig.json</c> checked in.
/// <para>
/// Canonical JSON is UTF-8, sorted keys, no whitespace. Team is intentionally
/// excluded so a team change never invalidates registry rows.
/// </para>
/// </summary>
internal static class ConnectionRefResolver
{
    public static string Compute(string organization, string project)
    {
        // Canonical JSON with sorted keys and no whitespace, per §5.1. The
        // configured strings are hashed verbatim — normalizing here would
        // put two contributors who checked in different shapes of the same
        // organization into two different registry rows, but T1's answer to
        // that is "one team, one manifest": the checked-in twig.json fixes
        // the canonical form. Storage-tier normalization defeats that
        // discipline silently.
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["organization"] = organization ?? string.Empty,
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
