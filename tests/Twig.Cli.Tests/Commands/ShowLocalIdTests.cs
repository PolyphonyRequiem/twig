using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowLocalIdTests
{
    private readonly StringWriter _stderr = new();
    private readonly IProviderWorkItemReadService _reader = Substitute.For<IProviderWorkItemReadService>();

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

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => CreateCommand().ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

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
    public async Task JsonAliases_KeepIdenticalShapeAndIndentation()
    {
        var localId = LocalWorkItemId.New();
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>())
            .Returns(CreateResult(localId, new ProviderRevisionToken("provider-token-9")));
        var command = CreateCommand();

        var outputs = new List<string>();
        foreach (var format in new[] { "json", "json-full", "json-compact" })
        {
            var (exitCode, output) = await StdoutCapture.RunAsync(
                () => command.ExecuteAsync(outputFormat: format, localIdText: localId.ToString()));
            exitCode.ShouldBe(0);
            outputs.Add(output);
        }

        outputs.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        outputs[0].ShouldContain(Environment.NewLine + "  \"localId\"");
        using var document = JsonDocument.Parse(outputs[0]);
        document.RootElement.GetProperty("providerRevision").GetString().ShouldBe("provider-token-9");
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("tree")]
    [InlineData("refresh")]
    [InlineData("fields")]
    [InlineData("sections")]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public async Task LocalIdRead_RejectsIncompatibleOptionsBeforeReading(string conflict)
    {
        var localId = LocalWorkItemId.New();
        var command = CreateCommand();

        var (exitCode, output) = await StdoutCapture.RunAsync(() => conflict switch
        {
            "integer" => command.ExecuteAsync(id: 42, outputFormat: "json", localIdText: localId.ToString()),
            "tree" => command.ExecuteAsync(tree: true, outputFormat: "json", localIdText: localId.ToString()),
            "refresh" => command.ExecuteAsync(refresh: true, outputFormat: "json", localIdText: localId.ToString()),
            "fields" => command.ExecuteAsync(outputFormat: "json", fields: "System.Title", localIdText: localId.ToString()),
            "sections" => command.ExecuteAsync(outputFormat: "json", sections: "links", localIdText: localId.ToString()),
            _ => command.ExecuteAsync(outputFormat: conflict, localIdText: localId.ToString()),
        });

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        _stderr.ToString().ShouldNotBeEmpty();
        await _reader.DidNotReceive().ReadByLocalIdAsync(Arg.Any<LocalWorkItemId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalIdRead_RejectsNonCanonicalGuidBeforeCallingReader()
    {
        var command = CreateCommand();

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(outputFormat: "json", localIdText: Guid.NewGuid().ToString("D").ToUpperInvariant()));

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        JsonDocument.Parse(_stderr.ToString()).RootElement.GetProperty("error").GetString()
            .ShouldNotBeNull().ShouldContain("canonical lowercase GUIDv7");
        await _reader.DidNotReceive().ReadByLocalIdAsync(Arg.Any<LocalWorkItemId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalIdRead_ReportsNullResultAsFormatAwareError()
    {
        var localId = LocalWorkItemId.New();
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProviderWorkItemReadResult?>(null));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => CreateCommand().ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

        exitCode.ShouldBe(1);
        output.ShouldBeEmpty();
        JsonDocument.Parse(_stderr.ToString()).RootElement.GetProperty("error").GetString()
            .ShouldNotBeNull().ShouldContain("not found or has no identity");
    }

    [Fact]
    public async Task LocalIdRead_ReportsProviderFailureAsFormatAwareError()
    {
        var localId = LocalWorkItemId.New();
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ProviderWorkItemReadResult?>(
                new InvalidOperationException("provider snapshot failed")));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => CreateCommand().ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

        exitCode.ShouldBe(1);
        output.ShouldBeEmpty();
        JsonDocument.Parse(_stderr.ToString()).RootElement.GetProperty("error").GetString()
            .ShouldNotBeNull().ShouldContain("provider snapshot failed");
    }

    [Fact]
    public async Task LocalIdRead_PropagatesCancellation()
    {
        var localId = LocalWorkItemId.New();
        using var source = new CancellationTokenSource();
        source.Cancel();
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromCanceled<ProviderWorkItemReadResult?>(source.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateCommand().ExecuteAsync(outputFormat: "json", ct: source.Token, localIdText: localId.ToString()));
        await _reader.Received(1).ReadByLocalIdAsync(localId, source.Token);
    }

    [Fact]
    public async Task LocalIdRead_ReportsUnavailableProviderReadService()
    {
        var localId = LocalWorkItemId.New();
        var command = new ShowLocalIdCommand(null, new RendererFactory(), stderrWriter: _stderr);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

        exitCode.ShouldBe(1);
        output.ShouldBeEmpty();
        JsonDocument.Parse(_stderr.ToString()).RootElement.GetProperty("error").GetString()
            .ShouldNotBeNull().ShouldContain("not available in this composition");
    }

    [Fact]
    public async Task LocalIdRead_LabelsRowsProviderNativeOnlyForMatchingActiveDescriptor()
    {
        var descriptor = Twig.Domain.Capabilities.ProviderCapabilityCatalog.Ado;
        var matching = new ExternalWorkItemIdentity(descriptor.ProviderKind, "scope", "1");
        var other = new ExternalWorkItemIdentity("fixture", "scope", "2");
        var localId = LocalWorkItemId.New();
        var now = DateTimeOffset.UtcNow;
        var fields = FieldValues.Empty;
        var result = new ProviderWorkItemReadResult(
            matching,
            new LocalWorkItemRecord(localId, 1, fields, now, now, DeletedAt: null),
            null, fields, fields,
            [
                new ProviderWorkItemRelationship(matching, new ExternalWorkItemIdentity(descriptor.ProviderKind, "scope", "3"), LinkKind.ParentChild),
                new ProviderWorkItemRelationship(matching, other, LinkKind.ParentChild),
            ]);
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>()).Returns(result);
        var provider = Substitute.For<IWorkItemProvider>();
        provider.Capabilities.Returns(descriptor);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => new ShowLocalIdCommand(_reader, new RendererFactory(), stderrWriter: _stderr, provider: provider)
                .ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

        exitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var rows = document.RootElement.GetProperty("relationships");
        rows[0].GetProperty("origin").GetString().ShouldBe("provider-native");
        rows[1].TryGetProperty("origin", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task LocalIdRead_OmitsOriginWithoutActiveProvider()
    {
        var descriptor = Twig.Domain.Capabilities.ProviderCapabilityCatalog.Ado;
        var identity = new ExternalWorkItemIdentity(descriptor.ProviderKind, "scope", "1");
        var localId = LocalWorkItemId.New();
        var now = DateTimeOffset.UtcNow;
        var fields = FieldValues.Empty;
        _reader.ReadByLocalIdAsync(localId, Arg.Any<CancellationToken>()).Returns(new ProviderWorkItemReadResult(
            identity,
            new LocalWorkItemRecord(localId, 1, fields, now, now, DeletedAt: null),
            null, fields, fields,
            [new ProviderWorkItemRelationship(identity, new ExternalWorkItemIdentity(descriptor.ProviderKind, "scope", "3"), LinkKind.ParentChild)]));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => CreateCommand().ExecuteAsync(outputFormat: "json", localIdText: localId.ToString()));

        exitCode.ShouldBe(0);
        JsonDocument.Parse(output).RootElement.GetProperty("relationships")[0]
            .TryGetProperty("origin", out _).ShouldBeFalse();
    }

    private ShowLocalIdCommand CreateCommand() =>
        new(_reader, new RendererFactory(), stderrWriter: _stderr);

    private static ProviderWorkItemReadResult CreateResult(
        LocalWorkItemId localId,
        ProviderRevisionToken? revisionToken)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new ExternalWorkItemIdentity("fixture", "scope-7", "opaque/42");
        var fields = FieldValues.Empty.Set("System.Title", new TextValue("Provider title"));
        return new ProviderWorkItemReadResult(
            identity,
            new LocalWorkItemRecord(localId, 8, FieldValues.Empty, now, now, DeletedAt: null),
            revisionToken,
            fields,
            fields,
            []);
    }
}
