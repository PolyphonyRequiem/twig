using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.Claims;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Claims;

/// <summary>
/// AB#737 §Cross-cutting rules + AB#739 identity readback: the resolver
/// returns a holder only when BOTH display name AND stable uniqueName
/// come back non-empty from the authenticated connection.
/// </summary>
public sealed class ConnectionHolderResolverTests
{
    [Fact]
    public async Task Returns_holder_when_identity_service_supplies_display_and_unique_name()
    {
        var iteration = new FakeIteration { DisplayName = "Jane Doe", UniqueName = "jane@contoso.com" };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeTrue(res.Error);
        res.Value.Identity.ShouldBe("jane@contoso.com");
        res.Value.DisplayName.ShouldBe("Jane Doe");
        res.Value.UniqueName.ShouldBe("jane@contoso.com");
    }

    [Fact]
    public async Task Returns_failure_when_unique_name_missing()
    {
        var iteration = new FakeIteration { DisplayName = "Jane Doe", UniqueName = null };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe("holder-resolver-empty-unique-name");
    }

    [Fact]
    public async Task Returns_failure_when_display_missing()
    {
        var iteration = new FakeIteration { DisplayName = null, UniqueName = "jane@contoso.com" };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe("holder-resolver-empty-display-name");
    }

    [Fact]
    public async Task Returns_failure_when_iteration_service_throws_no_fallback_to_config()
    {
        var iteration = new FakeIteration { ThrowMessage = "auth-failed" };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldStartWith("holder-resolver-unavailable");
        res.Error.ShouldContain("auth-failed");
    }

    private sealed class FakeIteration : IIterationService
    {
        public string? DisplayName { get; set; }
        public string? UniqueName { get; set; }
        public string? ThrowMessage { get; set; }

        public Task<string?> GetAuthenticatedUserDisplayNameAsync(CancellationToken ct = default)
        {
            if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
            return Task.FromResult(DisplayName);
        }

        public Task<(string? DisplayName, string? UniqueName)> GetAuthenticatedUserIdentityAsync(CancellationToken ct = default)
        {
            if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
            return Task.FromResult<(string?, string?)>((DisplayName, UniqueName));
        }

        public Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> DetectTemplateNameAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string Path, bool IncludeChildren)>> GetTeamAreaPathsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItemTypeWithStates>> GetWorkItemTypesWithStatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProcessConfigurationData> GetProcessConfigurationAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FieldDefinition>> GetFieldDefinitionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TeamIteration>> GetTeamIterationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AreaTreeNode> GetAreaTreeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
