using System.Reflection;
using System.Text;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// Load-time validation of the reference profile — the T1 (AB#732) §7.1 error
/// identifiers surface on <see cref="Twig.Domain.Common.Result.Error"/>.
/// </summary>
/// <remarks>
/// Uses a substitute assembly that streams the profile JSON we hand it so each
/// case can perturb one field and exercise one identifier. If a future
/// refactor removes the ability to test against a byte payload, add the
/// counterpart test at a higher level — the identifiers are what T4 and downstream
/// tooling depend on.
/// </remarks>
public sealed class ReferenceProfileLoadTimeValidationTests
{
    [Fact]
    public void Valid_shipped_profile_loads()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        provider.Load().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Schema_invalid_when_profile_json_is_malformed()
    {
        var provider = ProviderFor("{ not-json }");
        var result = provider.Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Schema_invalid_when_required_field_absent()
    {
        // Missing "identity" entirely.
        var json = Mutate(@"""identity"": ""twig.reference-profile.hyperbright"",", "");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Hierarchy_violation_when_apex_role_wrong()
    {
        var json = Mutate(@"""apex"": [""initiative""]", @"""apex"": [""feature""]");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.HierarchyLockedVocabularyViolation);
    }

    [Fact]
    public void Link_kinds_not_canonical_when_reference_name_mutated()
    {
        var json = Mutate(
            "System.LinkTypes.Hierarchy-Forward",
            "System.LinkTypes.Hierarchy-Forward-Tampered");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.LinkKindsNotCanonical);
    }

    [Fact]
    public void Fingerprint_mismatch_when_bytes_dont_match_content()
    {
        var json = Mutate(
            EmbeddedFingerprintValue,
            "0000000000000000000000000000000000000000000000000000000000000000");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileFingerprintMismatch);
    }

    [Fact]
    public void Primary_scope_empty_when_role_list_empty()
    {
        var json = Mutate(
            @"""eligibleRoles"": [""initiative"", ""investigation"", ""feature"", ""bug"", ""task""]",
            @"""eligibleRoles"": []");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.PrimaryScopeEmptyAllowSet);
    }

    /// <summary>
    /// T1 §6.6's own identifier, reachable again. The strict canonical converter
    /// would otherwise reject an unknown role token during deserialization and
    /// flatten it to <c>profile-schema-invalid</c>, leaving
    /// <c>primary-scope-unknown-role</c> declared but dead — the same defect
    /// class AB#735 exists to remove.
    /// </summary>
    [Fact]
    public void Primary_scope_unknown_role_is_named_rather_than_flattened()
    {
        var json = Mutate(@"""eligibleRoles"": [""initiative""", @"""eligibleRoles"": [""objective""");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.PrimaryScopeUnknownRole);
    }

    /// <summary>
    /// A duplicated role yields a named error rather than an exception from the
    /// aggregate's role dictionary. The old check counted DISTINCT roles, so six
    /// entries with one repeat produced a five-element set, passed, and then threw.
    /// </summary>
    [Fact]
    public void Duplicate_role_entry_is_named_rather_than_throwing()
    {
        var json = Mutate(
            @"    {
      ""role"": ""task"",",
            @"    {
      ""role"": ""bug"",");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.RoleSetNotCanonical);
    }

    [Fact]
    public void Schema_invalid_when_schema_marker_is_wrong()
    {
        var json = Mutate("twig-reference-profile/v1", "twig-reference-profile/v2");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Schema_invalid_when_fingerprint_algorithm_is_wrong()
    {
        var json = Mutate("twig-profile-fp/v1", "sha256-raw");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    /// <summary>
    /// T1 §3.5 fixes each link kind's MEANING, not just its relation refs. The
    /// meaning is the row's semantic claim and the thing a reviewer reads it for.
    /// </summary>
    [Fact]
    public void Link_kinds_not_canonical_when_a_declared_meaning_is_mutated()
    {
        var json = Mutate(@"""meaning"": ""blocking-sequencing""", @"""meaning"": ""ordering""");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.LinkKindsNotCanonical);
    }

    /// <summary>
    /// A state missing half its pair is named, not coerced. Coercing a null
    /// category to <c>Unknown</c> would let a truncated profile load and then
    /// fail every live comparison, reporting a drift that does not exist.
    /// </summary>
    [Fact]
    public void Schema_invalid_when_a_state_omits_its_category()
    {
        var json = Mutate(
            @"{ ""name"": ""Doing"", ""category"": ""InProgress"" }",
            @"{ ""name"": ""Doing"" }");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Checksum_sidecar_absent_is_reported_as_a_missing_blob()
    {
        // An integrity guard whose evidence has vanished must not silently pass.
        var provider = new EmbeddedReferenceProfileProvider(
            ProfilePinSources.Matching(), new SingleResourceAssembly(ValidJson, checksum: null));
        var result = provider.Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileBlobNotFound);
    }

    [Fact]
    public void Checksum_sidecar_that_does_not_cover_the_shipped_bytes_is_rejected()
    {
        // A raw-byte edit that leaves the parsed document identical: the in-band
        // fingerprint hashes a re-serialized form and cannot see this, so the
        // sidecar is the only thing standing between a tampered release and a
        // clean load. Whitespace is the sharpest possible case for that claim.
        var reformatted = ValidJson.Replace("\n  \"identity\"", "\n\n  \"identity\"", StringComparison.Ordinal);
        reformatted.ShouldNotBe(ValidJson);

        var provider = new EmbeddedReferenceProfileProvider(
            ProfilePinSources.Matching(),
            new SingleResourceAssembly(reformatted, checksum: Sha256(ValidJson)));
        var result = provider.Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileFingerprintMismatch);
    }

    // ---- Fixture ------------------------------------------------------------

    private static EmbeddedReferenceProfileProvider ProviderFor(string profileJson) =>
        new(ProfilePinSources.Matching(), new SingleResourceAssembly(profileJson));

    /// <summary>
    /// Applies one perturbation to the valid profile, asserting the replacement
    /// actually fired.
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="string.Replace(string,string)"/> is silently a no-op when
    /// the needle is absent, so a case whose literal drifts out of the profile
    /// stops perturbing anything and starts asserting that the VALID profile
    /// produces an error — a test that fails for a reason unrelated to what it
    /// names. Every literal here duplicates profile content, so that drift is a
    /// matter of when, not whether.
    /// </remarks>
    private static string Mutate(string find, string replace)
    {
        var mutated = ValidJson.Replace(find, replace, StringComparison.Ordinal);
        mutated.ShouldNotBe(ValidJson, $"the profile no longer contains '{find}' — update this case");
        return mutated;
    }

    /// <summary>
    /// The shipped profile's declared <c>fingerprint.bytes</c>, read from the
    /// artifact rather than hardcoded so a profile release does not silently
    /// neuter the mismatch case above.
    /// </summary>
    /// <remarks>
    /// A property, not a static field: static field initializers run in
    /// DECLARATION order, so a field here would read <see cref="ValidJson"/>
    /// before its own initializer had run and capture null.
    /// </remarks>
    private static string EmbeddedFingerprintValue
    {
        get
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ValidJson);
            return doc.RootElement.GetProperty("fingerprint").GetProperty("bytes").GetString()!;
        }
    }

    /// <summary>
    /// The valid shipped profile JSON, loaded from the actual embedded resource
    /// once per test class. Mutation cases below rewrite parts of this string
    /// so we never diverge from the real profile schema by hand-typing it.
    /// </summary>
    private static readonly string ValidJson = LoadValidJson();

    private static string LoadValidJson()
    {
        var asm = typeof(EmbeddedReferenceProfileProvider).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedReferenceProfileProvider.ProfileResourceName)
            ?? throw new InvalidOperationException("The valid profile resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Assembly stub exposing the profile resource and its checksum sidecar.
    /// It has to derive from <see cref="Assembly"/> because the provider takes
    /// an <c>Assembly</c>, not an abstract byte-source.
    /// </summary>
    /// <remarks>
    /// The sidecar defaults to one that COVERS the supplied content, so a
    /// mutation case reaches the schema check it is actually about instead of
    /// stopping at the integrity gate. Passing an explicit value is how the two
    /// sidecar cases above drive that gate directly.
    /// </remarks>
    private sealed class SingleResourceAssembly : Assembly
    {
        private readonly byte[] _bytes;
        private readonly byte[]? _checksum;

        public SingleResourceAssembly(string content, string? checksum = "")
        {
            _bytes = Encoding.UTF8.GetBytes(content);
            _checksum = checksum is null
                ? null
                : Encoding.UTF8.GetBytes(checksum.Length == 0 ? Sha256(content) : checksum);
        }

        public override Stream? GetManifestResourceStream(string name)
        {
            if (name == EmbeddedReferenceProfileProvider.ProfileResourceName)
                return new MemoryStream(_bytes, writable: false);
            if (name == EmbeddedReferenceProfileProvider.ProfileChecksumResourceName && _checksum is not null)
                return new MemoryStream(_checksum, writable: false);
            return null;
        }
    }
}
