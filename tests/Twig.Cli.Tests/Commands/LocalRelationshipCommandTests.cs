using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class LocalRelationshipCommandTests
{
    private readonly StringWriter _stderr = new();

    [Fact]
    public async Task AddAndList_UseLocalRelationshipStoreAndKeepReadProviderIndependent()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        var (expectedSource, expectedTarget) = LocalRelationship.Normalize(LinkKind.Related, source.Id, target.Id);
        var descriptorReads = 0;
        var command = Create(localItems, service, () =>
        {
            descriptorReads++;
            return ProviderCapabilityCatalog.GitHub;
        });

        var (addExit, addOutput) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), target.Id.ToString(), "json"));

        addExit.ShouldBe(0);
        descriptorReads.ShouldBe(1);
        using (var added = JsonDocument.Parse(addOutput))
        {
            added.RootElement.GetProperty("status").GetString().ShouldBe("added");
            var relationship = added.RootElement.GetProperty("relationship");
            relationship.GetProperty("origin").GetString().ShouldBe("twig-local");
            relationship.GetProperty("storage").GetString().ShouldBe("local-only");
            relationship.GetProperty("publication").GetString().ShouldBe("not-applicable");
            relationship.GetProperty("source").GetString().ShouldBe(expectedSource.ToString());
            relationship.GetProperty("target").GetString().ShouldBe(expectedTarget.ToString());
        }

        var noProvider = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));
        var (listExit, listOutput) = await StdoutCapture.RunAsync(
            () => noProvider.ListAsync(source.Id.ToString(), "json"));

        listExit.ShouldBe(0);
        using var listed = JsonDocument.Parse(listOutput);
        var rows = listed.RootElement.GetProperty("localRelationships");
        rows.GetArrayLength().ShouldBe(1);
        rows[0].GetProperty("origin").GetString().ShouldBe("twig-local");
        rows[0].GetProperty("storage").GetString().ShouldBe("local-only");
        rows[0].GetProperty("publication").GetString().ShouldBe("not-applicable");
        rows[0].GetProperty("source").GetString().ShouldBe(expectedSource.ToString());
        rows[0].GetProperty("target").GetString().ShouldBe(expectedTarget.ToString());
    }

    [Fact]
    public async Task Unrelate_WorksAfterProviderChangeWithoutResolvingProvider()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        await service.AddLocalOnlyAsync(ProviderCapabilityCatalog.GitHub, source.Id, target.Id, LinkKind.Related);
        var command = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));

        var (removeExit, removeOutput) = await StdoutCapture.RunAsync(
            () => command.UnrelateAsync(target.Id.ToString(), source.Id.ToString(), "json"));

        removeExit.ShouldBe(0);
        JsonDocument.Parse(removeOutput).RootElement.GetProperty("status").GetString().ShouldBe("removed");
        (await repository.GetAsync(source.Id, target.Id, LinkKind.Related))!.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_RefusesNativeCapabilityAndDoesNotPersistAnEdge()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        var command = Create(localItems, service, () => ProviderCapabilityCatalog.Ado);

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), target.Id.ToString(), "json"));

        exitCode.ShouldBe(1);
        output.ShouldBeEmpty();
        using (var error = JsonDocument.Parse(_stderr.ToString()))
            (error.RootElement.GetProperty("error").GetString() ?? string.Empty)
                .ShouldContain(RelationshipRoute.LocalOnlyRequiresUnsupportedReason);
        (await repository.GetForItemsAsync([source.Id])).ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisteredAdd_UsesGitHubCatalogWithoutProviderScopeOrClient()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var relationships = new LocalRelationshipService(new SqliteLocalRelationshipRepository(store));
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        var tokens = new CountingTokenSource();
        var transportConstructions = 0;
        var config = new TwigConfiguration { Provider = "github" };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<ILocalWorkItemStore>(localItems);
        services.AddSingleton(relationships);
        services.AddSingleton<RendererFactory>();
        services.AddSingleton<IGitHubTokenSource>(tokens);
        services.AddSingleton(new GitHubTransportOverride(() =>
        {
            transportConstructions++;
            return new HttpClientHandler();
        }));
        services.AddTwigNetworkServices(config);
        services.AddTwigCommands();
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<LocalRelationshipCommand>();

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), target.Id.ToString(), "json"));

        exitCode.ShouldBe(0);
        using (var document = JsonDocument.Parse(output))
            document.RootElement.GetProperty("relationship").GetProperty("origin")
                .GetString().ShouldBe("twig-local");
        tokens.Calls.ShouldBe(0);
        transportConstructions.ShouldBe(0);
        (await new SqliteLocalRelationshipRepository(store)
            .GetForItemsAsync([source.Id])).Count.ShouldBe(1);
    }

    [Fact]
    public async Task RegisteredAdd_UnknownProviderFailsClosedInsteadOfAssumingUnsupported()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var relationships = new LocalRelationshipService(new SqliteLocalRelationshipRepository(store));
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        var config = new TwigConfiguration { Provider = "jira" };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<ILocalWorkItemStore>(localItems);
        services.AddSingleton(relationships);
        services.AddSingleton<RendererFactory>();
        services.AddSingleton<TextWriter>(_stderr);
        services.AddTwigNetworkServices(config);
        services.AddTwigCommands();
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<LocalRelationshipCommand>();

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), target.Id.ToString(), "json"));

        exitCode.ShouldBe(1);
        output.ShouldBeEmpty();
        using (var error = JsonDocument.Parse(_stderr.ToString()))
            (error.RootElement.GetProperty("error").GetString() ?? string.Empty)
                .ShouldContain("provider-unknown");
        (await new SqliteLocalRelationshipRepository(store)
            .GetForItemsAsync([source.Id])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_RejectsIdsFormatBeforeReadingCapabilityOrWriting()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        var descriptorReads = 0;
        var command = Create(localItems, service, () =>
        {
            descriptorReads++;
            return ProviderCapabilityCatalog.GitHub;
        });

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), target.Id.ToString(), "ids"));

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        descriptorReads.ShouldBe(0);
        _stderr.ToString().ShouldContain("Output format must be human, json, or minimal.");
        (await repository.GetForItemsAsync([source.Id])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_RequiresCanonicalIdsAndLiveLocalRecordsBeforeReadingCapability()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var deleted = await localItems.CreateAsync(FieldValues.Empty);
        await localItems.DeleteAsync(deleted.Id, deleted.Revision);
        var descriptorReads = 0;
        var command = Create(localItems, service, () =>
        {
            descriptorReads++;
            return ProviderCapabilityCatalog.GitHub;
        });

        var (invalidExit, invalidOutput) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString().ToUpperInvariant(), deleted.Id.ToString(), "json"));
        invalidExit.ShouldBe(2);
        invalidOutput.ShouldBeEmpty();
        descriptorReads.ShouldBe(0);

        foreach (var invalidIdentity in new[]
        {
            "00000000-0000-4000-8000-000000000001",
            "019a0000-0000-7000-0000-000000000001",
            "00000000-0000-0000-0000-000000000000",
        })
        {
            _stderr.GetStringBuilder().Clear();
            var (invalidVersionExit, invalidVersionOutput) = await StdoutCapture.RunAsync(
                () => command.AddRelatedAsync(source.Id.ToString(), invalidIdentity, "json"));
            invalidVersionExit.ShouldBe(2);
            invalidVersionOutput.ShouldBeEmpty();
            descriptorReads.ShouldBe(0);
            _stderr.ToString().ShouldContain("canonical lowercase GUIDv7 identity");
        }

        _stderr.GetStringBuilder().Clear();
        var missing = LocalWorkItemId.New();
        var (missingExit, missingOutput) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), missing.ToString(), "json"));
        missingExit.ShouldBe(1);
        missingOutput.ShouldBeEmpty();
        descriptorReads.ShouldBe(0);

        _stderr.GetStringBuilder().Clear();
        var (deletedExit, deletedOutput) = await StdoutCapture.RunAsync(
            () => command.AddRelatedAsync(source.Id.ToString(), deleted.Id.ToString(), "json"));
        deletedExit.ShouldBe(1);
        deletedOutput.ShouldBeEmpty();
        descriptorReads.ShouldBe(0);
    }

    [Fact]
    public async Task List_MinimalUsesOneTabSeparatedRowPerLocalEdge()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        await service.AddLocalOnlyAsync(ProviderCapabilityCatalog.GitHub, source.Id, target.Id, LinkKind.Related);
        var command = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ListAsync(source.Id.ToString(), "minimal"));

        exitCode.ShouldBe(0);
        var (expectedSource, expectedTarget) = LocalRelationship.Normalize(LinkKind.Related, source.Id, target.Id);
        output.Trim().ShouldBe($"{expectedSource}\t{expectedTarget}\trelated\ttwig-local\tlocal-only\tnot-applicable");
    }

    [Fact]
    public async Task List_HumanOutputLabelsLocalOnlyEdges()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var target = await localItems.CreateAsync(FieldValues.Empty);
        await service.AddLocalOnlyAsync(ProviderCapabilityCatalog.GitHub, source.Id, target.Id, LinkKind.Related);
        var command = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ListAsync(source.Id.ToString(), "human"));

        exitCode.ShouldBe(0);
        output.ShouldContain($"Twig-local Related links for {source.Id}:");
        var (expectedSource, expectedTarget) = LocalRelationship.Normalize(LinkKind.Related, source.Id, target.Id);
        output.ShouldContain($"{expectedSource} --related-- {expectedTarget}");
        output.ShouldContain("twig-local; local-only; not-applicable");
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("019a0000-0000-7000-8000-00000000000A")]
    [InlineData("019a0000-0000-7000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task List_RejectsNonCanonicalOrNonVersion7Identity(string localId)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var service = new LocalRelationshipService(new SqliteLocalRelationshipRepository(store));
        var command = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ListAsync(localId, "json"));

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        _stderr.ToString().ShouldContain("canonical lowercase GUIDv7 identity");
    }

    [Fact]
    public async Task List_RejectsIdsFormatBecauseLocalIdentitiesAreNotNumericIds()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var localItems = new SqliteLocalWorkItemRepository(store);
        var repository = new SqliteLocalRelationshipRepository(store);
        var service = new LocalRelationshipService(repository);
        var source = await localItems.CreateAsync(FieldValues.Empty);
        var command = Create(localItems, service, () => throw new InvalidOperationException("provider must not be resolved"));

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => command.ListAsync(source.Id.ToString(), "ids"));

        exitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        _stderr.ToString().ShouldContain("Output format must be human, json, or minimal.");
    }

    private LocalRelationshipCommand Create(
        ILocalWorkItemStore localItems,
        LocalRelationshipService service,
        Func<ProviderCapabilityDescriptor> descriptor) =>
        new(localItems, service, descriptor, new RendererFactory(), stderrWriter: _stderr);

    private sealed class CountingTokenSource : IGitHubTokenSource
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromException<string>(new InvalidOperationException("Unexpected token access."));
        }
    }
}
