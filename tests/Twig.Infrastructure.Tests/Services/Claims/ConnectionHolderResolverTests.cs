using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.Claims;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Claims;

/// <summary>
/// AB#737 §Cross-cutting rules forbids the mint path from inferring holder
/// identity from a config default, an OS username, or any other ambient
/// signal. The resolver's contract is: the authenticated connection
/// identity IS the holder, and any failure to observe it MUST return
/// <see cref="Twig.Domain.Common.Result"/> failure so the claim service
/// reports <c>HolderUnavailable</c>.
/// </summary>
public sealed class ConnectionHolderResolverTests
{
    [Fact]
    public async Task Returns_holder_when_iteration_service_supplies_display_name()
    {
        var iteration = new FakeIteration { DisplayName = "Jane Doe" };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeTrue(res.Error);
        res.Value.Identity.ShouldBe("Jane Doe");
        res.Value.DisplayName.ShouldBe("Jane Doe");
    }

    [Fact]
    public async Task Returns_failure_when_iteration_service_returns_null()
    {
        var iteration = new FakeIteration { DisplayName = null };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe("holder-resolver-empty");
    }

    [Fact]
    public async Task Returns_failure_when_iteration_service_returns_whitespace()
    {
        var iteration = new FakeIteration { DisplayName = "   " };
        var resolver = new ConnectionHolderResolver(iteration);
        var res = await resolver.ResolveAsync();
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe("holder-resolver-empty");
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
        public string? ThrowMessage { get; set; }

        public Task<string?> GetAuthenticatedUserDisplayNameAsync(CancellationToken ct = default)
        {
            if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
            return Task.FromResult(DisplayName);
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
