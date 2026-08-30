using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// Integrity and canonical-spelling guards on the SHIPPED profile artifact
/// (AB#735). These assert properties of the released files themselves, not of
/// the loader.
/// </summary>
/// <remarks>
/// The T1 (AB#732) §3 schema is normative and the shipped document is supposed
/// to be an instance of it. Before AB#735 nothing checked that: the profile
/// spelled roles <c>Initiative</c> and link kinds <c>ParentChild</c> while the
/// schema spelled them <c>initiative</c> and <c>parent-child</c>, the enum
/// converter accepted both, and every test passed. A normative schema nothing
/// compares against is documentation, not a contract — so these tests compare.
/// </remarks>
public sealed class ReferenceProfileArtifactTests
{
    private static byte[] ProfileBytes()
    {
        var asm = typeof(EmbeddedReferenceProfileProvider).Assembly;
        using var stream = asm.GetManifestResourceStream(
            EmbeddedReferenceProfileProvider.ProfileResourceName)
            ?? throw new InvalidOperationException("profile.json resource missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string SidecarText()
    {
        var asm = typeof(EmbeddedReferenceProfileProvider).Assembly;
        using var stream = asm.GetManifestResourceStream(
            EmbeddedReferenceProfileProvider.ProfileChecksumResourceName)
            ?? throw new InvalidOperationException("profile.json.sha256 resource missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>
    /// The in-band <c>fingerprint.bytes</c> agrees with its own recomputation.
    /// The failure message carries the correct value so a profile edit is a
    /// copy-paste away from correct rather than a puzzle.
    /// </summary>
    [Fact]
    public void Declared_fingerprint_matches_the_recomputation()
    {
        var raw = ProfileBytes();
        using var doc = JsonDocument.Parse(raw);
        var declared = doc.RootElement.GetProperty("fingerprint").GetProperty("bytes").GetString();

        var recomputed = ReferenceProfileFingerprint.ComputeEmbeddedFingerprint(raw);

        declared.ShouldBe(recomputed,
            $"profile.json fingerprint.bytes is stale — set it to {recomputed}");
    }

    /// <summary>
    /// The T1 §1 sidecar covers the shipped bytes exactly. This is the guard
    /// that sees raw-byte drift; the in-band fingerprint hashes a re-serialized
    /// form and structurally cannot.
    /// </summary>
    [Fact]
    public void Checksum_sidecar_covers_the_shipped_bytes()
    {
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(ProfileBytes()));

        SidecarText().ShouldBe(expected,
            $"profile.json.sha256 is stale — set it to {expected}");
    }

    /// <summary>
    /// The shipped profile carries no carriage returns.
    /// </summary>
    /// <remarks>
    /// 🔴 A byte-exact checksum over a file git is allowed to normalize is not
    /// reproducible across platforms. With autocrlf on, a Windows checkout
    /// rewrites LF to CRLF, the embedded resource then carries different bytes
    /// than the sidecar pins, and the integrity check fails for every Windows
    /// user. Verified rather than theorized: CI hashed the CRLF form to
    /// <c>6d747175…</c> against the LF pin <c>d2cf4c20…</c>.
    /// <para>
    /// <c>.gitattributes</c> pins both files to <c>eol=lf</c>; this test is the
    /// cheap local detector, because the failure otherwise only reproduces on a
    /// Windows checkout and would be found by CI or a user rather than by the
    /// person making the change. Note the in-band <c>fingerprint.bytes</c>
    /// CANNOT catch this — it re-serializes before hashing, which is exactly
    /// the blindness the sidecar exists to cover.
    /// </para>
    /// </remarks>
    [Fact]
    public void Profile_and_sidecar_carry_no_carriage_returns()
    {
        ProfileBytes().ShouldNotContain((byte)'\r',
            "profile.json must stay LF-only; see .gitattributes");

        var asm = typeof(EmbeddedReferenceProfileProvider).Assembly;
        using var stream = asm.GetManifestResourceStream(
            EmbeddedReferenceProfileProvider.ProfileChecksumResourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.ToArray().ShouldNotContain((byte)'\r',
            "profile.json.sha256 must stay LF-only; see .gitattributes");
    }

    /// <summary>
    /// Roles and link kinds are spelled as T1 §3 and §3.5 spell them, read as
    /// raw JSON string TOKENS.
    /// </summary>
    /// <remarks>
    /// 🔴 Token values, not the parsed model, deliberately. Parsing normalizes:
    /// the converter maps a wire token onto a CLR enum member, so an assertion
    /// over <c>profile.Types[i].Role</c> is true for every spelling the reader
    /// accepts and is therefore blind to precisely the divergence this test
    /// exists to catch.
    /// <para>
    /// Read through <see cref="JsonDocument"/> rather than by substring so the
    /// assertion survives reformatting. A substring like
    /// <c>"role": "initiative"</c> bakes in the space after the colon and would
    /// fail on a semantically identical document — a test that breaks on
    /// whitespace trains people to edit the test.
    /// </para>
    /// </remarks>
    [Fact]
    public void Profile_uses_the_canonical_T1_role_spellings()
    {
        using var doc = JsonDocument.Parse(ProfileBytes());
        var root = doc.RootElement;

        root.GetProperty("types").EnumerateArray()
            .Select(t => t.GetProperty("role").GetString())
            .ShouldBe(["initiative", "investigation", "feature", "bug", "task"]);

        root.GetProperty("primaryScope").GetProperty("eligibleRoles").EnumerateArray()
            .Select(r => r.GetString())
            .ShouldBe(["initiative", "investigation", "feature", "bug", "task"]);

        var hierarchy = root.GetProperty("hierarchy");
        hierarchy.GetProperty("apex").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(["initiative"]);
        hierarchy.GetProperty("requirement").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(["investigation", "feature", "bug"]);
        hierarchy.GetProperty("leaf").EnumerateArray().Select(r => r.GetString())
            .ShouldBe(["task"]);
    }

    [Fact]
    public void Profile_uses_the_canonical_T1_link_kind_spellings()
    {
        using var doc = JsonDocument.Parse(ProfileBytes());

        doc.RootElement.GetProperty("linkKinds").EnumerateArray()
            .Select(k => k.GetProperty("kind").GetString())
            .ShouldBe(["parent-child", "predecessor-successor", "related", "artifact"]);
    }

    /// <summary>
    /// The document declares the schema and fingerprint algorithm T1 fixes.
    /// </summary>
    [Fact]
    public void Profile_declares_the_pinned_schema_and_algorithm()
    {
        using var doc = JsonDocument.Parse(ProfileBytes());

        doc.RootElement.GetProperty("$schema").GetString()
            .ShouldBe(EmbeddedReferenceProfileProvider.ProfileSchemaVersion);
        doc.RootElement.GetProperty("fingerprint").GetProperty("algorithm").GetString()
            .ShouldBe(EmbeddedReferenceProfileProvider.FingerprintAlgorithm);
    }
    /// <summary>
    /// The converter now REFUSES the old spelling rather than tolerating it.
    /// Without this the reconciliation is a convention; with it, it is enforced.
    /// </summary>
    [Fact]
    public void Pre_reconciliation_link_kind_spelling_no_longer_parses()
    {
        var raw = Encoding.UTF8.GetString(ProfileBytes())
            .Replace("\"kind\": \"parent-child\"", "\"kind\": \"ParentChild\"", StringComparison.Ordinal);

        var result = new EmbeddedReferenceProfileProvider(
            ProfilePinSources.Matching(),
            new StubAssembly(raw)).Load();

        result.IsSuccess.ShouldBeFalse("the converter must not accept two spellings for one link kind");
        result.Error.ShouldBe(Twig.Domain.ValueObjects.ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    private sealed class StubAssembly(string content) : System.Reflection.Assembly
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public override Stream? GetManifestResourceStream(string name)
        {
            if (name == EmbeddedReferenceProfileProvider.ProfileResourceName)
                return new MemoryStream(_bytes, writable: false);
            if (name == EmbeddedReferenceProfileProvider.ProfileChecksumResourceName)
                return new MemoryStream(Encoding.UTF8.GetBytes(
                    Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(_bytes))), writable: false);
            return null;
        }
    }
}
