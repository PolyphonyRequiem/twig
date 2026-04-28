using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Integration tests for <see cref="AdoRestClient"/> and <see cref="AdoIterationService"/>.
/// Requires a personal ADO test org. Skipped in CI — run manually.
/// </summary>
/// <remarks>
/// To run these tests, set the following environment variables:
///   TWIG_TEST_ORG    = https://dev.azure.com/yourorg
///   TWIG_TEST_PROJECT = YourProject
///   TWIG_PAT         = your-personal-access-token
///
/// Or configure a test-config.md file (see ITEM-072).
/// </remarks>
[Trait("Category", "Integration")]
public class AdoRestClientIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AdoRestClientIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool CanRun =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TWIG_TEST_ORG")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TWIG_TEST_PROJECT")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TWIG_PAT"));

    private void SkipIfNotConfigured()
    {
        if (!CanRun)
            _output.WriteLine("SKIPPED: Set TWIG_TEST_ORG, TWIG_TEST_PROJECT, TWIG_PAT env vars to run.");
    }

    private static (AdoRestClient client, AdoIterationService iterationService) CreateClients()
    {
        var org = Environment.GetEnvironmentVariable("TWIG_TEST_ORG")!;
        var project = Environment.GetEnvironmentVariable("TWIG_TEST_PROJECT")!;
        var auth = new PatAuthProvider();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new AdoRestClient(http, auth, org, project, new WorkItemMapper());
        var team = Environment.GetEnvironmentVariable("TWIG_TEST_TEAM") ?? $"{project} Team";
        var iterationService = new AdoIterationService(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, auth, org, project, team);
        return (client, iterationService);
    }

    [Fact]
    public async Task FetchAsync_ExistingWorkItem_ReturnsWorkItem()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (client, _) = CreateClients();

        // Create a work item first to avoid assuming ID 1 exists
        var seed = new WorkItemBuilder(-1, $"Fetch test {DateTimeOffset.UtcNow:O}").AsSeed().Build();
        var id = await client.CreateAsync(seed.ToCreateRequest());

        var item = await client.FetchAsync(id);

        item.Id.ShouldBe(id);
        item.Title.ShouldNotBeNullOrEmpty();
        item.Revision.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAsync_NewWorkItem_ReturnsId()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (client, _) = CreateClients();

        var seed = new WorkItemBuilder(-1, $"Integration test task {DateTimeOffset.UtcNow:O}").AsSeed().Build();
        var id = await client.CreateAsync(seed.ToCreateRequest());

        id.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PatchAsync_ExistingWorkItem_UpdatesRevision()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (client, _) = CreateClients();

        // Create a work item first, then patch it
        var seed = new WorkItemBuilder(-1, $"Patch test {DateTimeOffset.UtcNow:O}").AsSeed().Build();
        var id = await client.CreateAsync(seed.ToCreateRequest());

        var item = await client.FetchAsync(id);
        var changes = new List<FieldChange> { new("System.Title", item.Title, "Updated title") };
        var newRev = await client.PatchAsync(id, changes, item.Revision);

        newRev.ShouldBeGreaterThan(item.Revision);
    }

    [Fact]
    public async Task AddCommentAsync_ExistingWorkItem_Succeeds()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (client, _) = CreateClients();

        var seed = new WorkItemBuilder(-1, $"Comment test {DateTimeOffset.UtcNow:O}").AsSeed().Build();
        var id = await client.CreateAsync(seed.ToCreateRequest());

        // Should not throw
        await client.AddCommentAsync(id, "Integration test comment.");
    }

    [Fact]
    public async Task QueryByWiqlAsync_SelectAll_ReturnsIds()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (client, _) = CreateClients();

        var ids = await client.QueryByWiqlAsync("SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] = 'Task'");

        ids.ShouldNotBeNull();
        // There should be at least one task in any seeded test org
        ids.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GetCurrentIterationAsync_ReturnsIteration()
    {
        if (!CanRun) { SkipIfNotConfigured(); return; }
        var (_, iterationService) = CreateClients();

        var iteration = await iterationService.GetCurrentIterationAsync();

        iteration.Value.ShouldNotBeNullOrEmpty();
    }
}
