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
        var provider = new EmbeddedReferenceProfileProvider();
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
        var json = ValidJson.Replace(@"""identity"": ""twig.reference-profile.hyperbright"",", "");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Hierarchy_violation_when_apex_role_wrong()
    {
        var json = ValidJson.Replace(@"""apex"": [""Initiative""]", @"""apex"": [""Feature""]");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.HierarchyLockedVocabularyViolation);
    }

    [Fact]
    public void Link_kinds_not_canonical_when_reference_name_mutated()
    {
        var json = ValidJson.Replace(
            "System.LinkTypes.Hierarchy-Forward",
            "System.LinkTypes.Hierarchy-Forward-Tampered");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.LinkKindsNotCanonical);
    }

    [Fact]
    public void Fingerprint_mismatch_when_bytes_dont_match_content()
    {
        var json = ValidJson.Replace(
            "\"bytes\": \"cc852619d3c63a49293a4da7554456898be0acfc0d260b7abad844b022c64061\"",
            "\"bytes\": \"0000000000000000000000000000000000000000000000000000000000000000\"");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileFingerprintMismatch);
    }

    [Fact]
    public void Primary_scope_empty_when_role_list_empty()
    {
        var json = ValidJson.Replace(
            @"""eligibleRoles"": [""Initiative"", ""Investigation"", ""Feature"", ""Bug"", ""Task""]",
            @"""eligibleRoles"": []");
        var result = ProviderFor(json).Load();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.PrimaryScopeEmptyAllowSet);
    }

    // ---- Fixture ------------------------------------------------------------

    private static EmbeddedReferenceProfileProvider ProviderFor(string profileJson) =>
        new(new SingleResourceAssembly(profileJson));

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

    /// <summary>
    /// Assembly stub that exposes ONE resource stream under the expected name.
    /// It has to derive from <see cref="Assembly"/> because the provider takes
    /// an <c>Assembly</c>, not an abstract byte-source.
    /// </summary>
    private sealed class SingleResourceAssembly(string content) : Assembly
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public override Stream? GetManifestResourceStream(string name) =>
            name == EmbeddedReferenceProfileProvider.ProfileResourceName
                ? new MemoryStream(_bytes, writable: false)
                : null;
    }
}
