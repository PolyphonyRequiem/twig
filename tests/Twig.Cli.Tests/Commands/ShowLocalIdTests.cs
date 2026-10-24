using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowLocalIdTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-local-id-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _stderr = new();
    private readonly IProviderWorkItemReadService _reader = Substitute.For<IProviderWorkItemReadService>();

    public ShowLocalIdTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _stderr.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task LocalIdRead_EmitsProviderAndComposedFieldsWithoutIntegerAlias()
    {
        var localId = LocalWorkItemId.New();
        var identity = new ExternalWorkItemIdentity("fixture", "scope-7", "opaque/42");
        var providerFields = FieldValues.Empty
            .Set("System.Title", new TextValue("Provider title"))
            .Set("System.Description", new TextValue("Provider description"));
        var localFields = FieldValues.Empty
            .Set("System.Title", new TextValue("Local title"))
            .Set("System.Description", NullValue.Instance);
        var now = DateTimeOffset.UtcNow;
        var result = new ProviderWorkItemReadResult(
            identity,
            new LocalWorkItemRecord(localId, 8, localFields, now, now, DeletedAt: null),
            null,
            providerFields,
            providerFields.Set("System.Title", new TextValue("Local title"))
                .Set("System.Description", NullValue.Instance),
            [new ProviderWorkItemRelationship(
                identity,
                new ExternalWorkItemIdentity("fixture", "scope-7", "opaque/parent"),
                LinkKind.ParentChild)]);
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>()).Returns(result);

        var command = CreateCommand();
        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(outputFormat: "json", localId: localId.ToString()));

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("localId").GetString().ShouldBe(localId.ToString());
        root.GetProperty("localRevision").GetInt64().ShouldBe(8);
        root.GetProperty("providerRevision").ValueKind.ShouldBe(JsonValueKind.Null);
        root.TryGetProperty("id", out _).ShouldBeFalse();
        root.GetProperty("providerIdentity").GetProperty("nativeId").GetString().ShouldBe("opaque/42");
        root.GetProperty("providerFields").GetProperty("System.Title").GetProperty("value").GetString()
            .ShouldBe("Provider title");
        root.GetProperty("fields").GetProperty("System.Title").GetProperty("value").GetString()
            .ShouldBe("Local title");
        root.GetProperty("fields").GetProperty("System.Description").GetProperty("kind").GetString()
            .ShouldBe("null");
        root.GetProperty("relationships")[0].GetProperty("kind").GetString().ShouldBe("parent-child");
        root.GetProperty("relationships")[0].GetProperty("target").GetProperty("nativeId").GetString()
            .ShouldBe("opaque/parent");
        await _reader.Received(1).ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalIdRead_RejectsNonCanonicalGuidBeforeCallingReader()
    {
        var command = CreateCommand();

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(outputFormat: "json", localId: Guid.NewGuid().ToString("D").ToUpperInvariant()));

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        _stderr.ToString().ShouldContain("canonical lowercase GUIDv7");
        await _reader.DidNotReceive().ReadByLocalIdAsync(Arg.Any<LocalWorkItemId>(), Arg.Any<CancellationToken>());
    }

    private ShowCommand CreateCommand()
    {
        var workItems = Substitute.For<IWorkItemRepository>();
        var pending = Substitute.For<IPendingChangeStore>();
        var sync = new SyncCoordinatorFactory(
            workItems,
            Substitute.For<IAdoWorkItemService>(),
            new ProtectedCacheWriter(workItems, pending),
            pending,
            null,
            30,
            30);
        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        var context = new CommandContext(
            pipelineFactory,
            formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration(),
            Stderr: _stderr);
        var paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));
        return new ShowCommand(
            context,
            workItems,
            Substitute.For<IWorkItemLinkRepository>(),
            sync,
            new StatusFieldConfigReader(paths),
            providerWorkItemReadService: _reader);
    }
}
