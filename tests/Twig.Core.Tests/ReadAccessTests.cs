using System.Collections.Immutable;
using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Core.Tests;

public sealed class ReadAccessTests
{
    [Fact]
    public async Task ProvidersWithoutExplicitOnlineValidationFailClosed()
    {
        IProvider provider = new UnsupportedProvider();
        var result = await provider.ValidateReadAccessAsync(new(new("test"), "scope", "target", "actor"), default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unsupported);
    }

    private sealed class UnsupportedProvider : IProvider
    {
        public ProviderCapabilities Capabilities => throw new NotSupportedException();
        public Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct, bool captureOnly = false) => throw new NotSupportedException();
        public Task<Result<long>> ImportAsync(Plan plan, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<Receipt>> ApplyAsync(Plan plan, int operation, Guid attempt, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<Receipt>> VerifyAsync(Plan plan, Receipt acknowledgement, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<Receipt>> RecoverAsync(Plan plan, int operation, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
