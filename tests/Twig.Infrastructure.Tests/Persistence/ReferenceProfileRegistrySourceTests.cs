using NSubstitute;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// The T3 cutover (AB#727). Before it, the only registered
/// <c>IProfileRegistrySource</c> failed closed, so <c>twig init</c> could not
/// bind a workspace in any fresh worktree.
/// </summary>
public sealed class ReferenceProfileRegistrySourceTests
{
    [Fact]
    public void Resolve_materializes_identity_and_version_from_the_embedded_profile()
    {
        var source = new ReferenceProfileRegistrySource(new EmbeddedReferenceProfileProvider());

        var result = source.Resolve("anyProcess");

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.Identity.ShouldBe("twig.reference-profile.hyperbright");
        result.Value.Version.ShouldBe("1.0.0");
    }

    /// <summary>
    /// The allow-set is the concrete type-name projection of the profile's
    /// eligible roles — the shape AB#738's eligibility gate consumes. Order is
    /// role declaration order, per <c>PrimaryScopeAllowTypeNames</c>.
    /// </summary>
    [Fact]
    public void Resolve_materializes_the_concrete_primary_scope_allow_set()
    {
        var source = new ReferenceProfileRegistrySource(new EmbeddedReferenceProfileProvider());

        var result = source.Resolve("anyProcess");

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.PrimaryScopeTypes.ShouldBe(
            new[] { "Initiative", "Investigation", "Feature", "Bug", "Task" });
    }

    /// <summary>
    /// A profile that fails to load must surface its own named identifier, not a
    /// fabricated identity/version. T1 §6.3: no synthetic identity, no partial
    /// workspace.
    /// </summary>
    [Fact]
    public void Resolve_propagates_the_profile_load_error_verbatim()
    {
        var provider = Substitute.For<IReferenceProfileProvider>();
        provider.Load().Returns(
            Result.Fail<Twig.Domain.ValueObjects.ReferenceProfile>("profile-schema-unknown"));

        var result = new ReferenceProfileRegistrySource(provider).Resolve("anyProcess");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("profile-schema-unknown");
    }

    /// <summary>
    /// Selection is pinned by the running binary, not derived from the live
    /// process template, so the argument must not change the materialization.
    /// </summary>
    [Theory]
    [InlineData("Basic")]
    [InlineData("Agile")]
    [InlineData("")]
    public void Resolve_is_independent_of_the_process_template_argument(string processTemplate)
    {
        var source = new ReferenceProfileRegistrySource(new EmbeddedReferenceProfileProvider());

        var result = source.Resolve(processTemplate);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.Identity.ShouldBe("twig.reference-profile.hyperbright");
    }
}
