using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class FieldDefinitionSyncServiceTests
{
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

    [Fact]
    public async Task SyncAsync_FetchesAndSaves()
    {
        var definitions = new List<FieldDefinition>
        {
            new("System.Title", "Title", "string", false),
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        };
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(definitions);

        var count = await FieldDefinitionSyncService.SyncAsync(_iterationService, _fieldDefinitionStore);

        count.ShouldBe(2);
        await _fieldDefinitionStore.Received(1).SaveBatchAsync(
            Arg.Is<IReadOnlyList<FieldDefinition>>(d => d.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_EmptyResponse_ReturnsZeroAndDoesNotSave()
    {
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var count = await FieldDefinitionSyncService.SyncAsync(_iterationService, _fieldDefinitionStore);

        count.ShouldBe(0);
        await _fieldDefinitionStore.DidNotReceive().SaveBatchAsync(
            Arg.Any<IReadOnlyList<FieldDefinition>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        await FieldDefinitionSyncService.SyncAsync(_iterationService, _fieldDefinitionStore, cts.Token);

        await _iterationService.Received(1).GetFieldDefinitionsAsync(cts.Token);
    }
}
