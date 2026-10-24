using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// #1186 A+R: workspace/status composes durable local values for DISPLAY only. Membership,
/// ordering, ids, state buckets, footers and assignee grouping stay on the mirror, so a local edit
/// that would change selection criteria changes what a row shows but never which rows exist.
/// The origin-bound browser document stays raw observed evidence.
/// </summary>
public sealed partial class ShowDurableFieldOverlayTests
{
    /// <summary>The workspace command resolves the authentication provider for its browser path.</summary>
    private ServiceProvider ComposeWorkspace(bool optedIn, Spectre.Console.IAnsiConsole? liveConsole = null, Action<IServiceCollection>? replace = null) =>
        Compose(optedIn, liveConsole, SprintOne(), services =>
        {
            services.AddSingleton(Substitute.For<IAuthenticationProvider>());
            replace?.Invoke(services);
        });

    private static IIterationService SprintOne()
    {
        var iterations = IdentityStubs.NewBound();
        iterations.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse(@"Project\Sprint 1").Value);
        return iterations;
    }

    private static async Task SaveMemberAsync(ServiceProvider services, int itemId, string title, string iteration)
    {
        var item = new WorkItemBuilder(itemId, title).InState("Active").WithIterationPath(iteration)
            .AssignedTo(IdentityStubs.DefaultDisplayName)
            .AssignedToUniqueName(IdentityStubs.DefaultCanonicalPrincipal)
            .Build();
        item.MarkSynced(3);
        await services.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
    }

    private async Task EditLocallyAsync(int itemId, FieldValues fields)
    {
        var local = new SqliteLocalWorkItemRepository(_store);
        var id = (await local.FindAsync(new ExternalWorkItemIdentity("ado", Scope, itemId.ToString(System.Globalization.CultureInfo.InvariantCulture))))!.Value;
        var current = (await local.GetAsync(id))!;
        (await local.UpdateAsync(id, current.Revision, fields)).Value.ShouldBeOfType<LocalWriteApplied>();
    }

    /// <summary>
    /// #42 and #44 are mirror members of Sprint 1. #42 is edited locally in every way that WOULD
    /// change selection (closed, moved to Sprint 2, reassigned); #43 is a Sprint 2 mirror item edited
    /// locally INTO Sprint 1 and onto self. Display-only composition must not move either.
    /// </summary>
    private async Task SeedWorkspaceAsync(ServiceProvider services, bool editLocally)
    {
        await SaveMemberAsync(services, 42, "Remote member", @"Project\Sprint 1");
        await SaveMemberAsync(services, 43, "Remote outsider", @"Project\Sprint 2");
        await SaveMemberAsync(services, 44, "Untouched member", @"Project\Sprint 1");
        if (!editLocally) return;

        await EditLocallyAsync(42, FieldValues.Empty
            .Set("System.Title", new TextValue("Local member"))
            .Set("System.State", new TextValue("Closed"))
            .Set("System.IterationPath", new TextValue(@"Project\Sprint 2"))
            .Set("System.AssignedTo", new IdentityValue("other@tests.twig", "Other Person")));
        await EditLocallyAsync(43, FieldValues.Empty
            .Set("System.Title", new TextValue("Local outsider"))
            .Set("System.IterationPath", new TextValue(@"Project\Sprint 1"))
            .Set("System.AssignedTo", new IdentityValue(IdentityStubs.DefaultCanonicalPrincipal, IdentityStubs.DefaultDisplayName)));
    }

    private static async Task<string> WorkspaceAsync(ServiceProvider services, string format, bool sprintLayout = false, bool flat = false)
    {
        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(format, noLive: true, sprintLayout: sprintLayout, flat: flat));
        exit.ShouldBe(0, output);
        return output;
    }

    private static Dictionary<long, JsonElement> Records(string json)
    {
        var records = new Dictionary<long, JsonElement>();
        using var doc = JsonDocument.Parse(json);
        Collect(doc.RootElement);
        return records;

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                    && element.TryGetProperty("title", out _))
                    records[id.GetInt64()] = element.Clone();
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    Collect(child);
            }
        }
    }

    private static string Cell(JsonElement record, string name) => record.GetProperty(name).GetString()!;

    private async Task AssertMirrorUntouchedAsync(ServiceProvider services)
    {
        var mirror = services.GetRequiredService<IWorkItemRepository>();
        var member = (await mirror.GetByIdAsync(42))!;
        member.Title.ShouldBe("Remote member");
        member.State.ShouldBe("Active");
        member.IterationPath.ToString().ShouldBe(@"Project\Sprint 1");
        member.IsDirty.ShouldBeFalse();
        (await mirror.GetByIdAsync(43))!.IterationPath.ToString().ShouldBe(@"Project\Sprint 2");
    }

    [Fact]
    public async Task OptedIn_WorkspaceJsonShowsDurableCells_WhileMembershipOrderAndIdsStayOnTheMirror()
    {
        using var services = ComposeWorkspace(optedIn: true);
        await SeedWorkspaceAsync(services, editLocally: true);
        var before = DurableRows();

        var json = await WorkspaceAsync(services, "json");
        (await WorkspaceAsync(services, "json")).ShouldBe(json);
        var ids = await WorkspaceAsync(services, "ids");

        var records = Records(json);
        records.Keys.Order().ToArray().ShouldBe(new long[] { 42, 44 }, "a local edit into Sprint 1 must not add #43; one out of Sprint 1 must not drop #42");
        Cell(records[42], "title").ShouldBe("Local member");
        Cell(records[42], "state").ShouldBe("Closed");
        Cell(records[42], "assignedTo").ShouldBe("Other Person");
        Cell(records[44], "title").ShouldBe("Untouched member");
        json.ShouldNotContain("Local outsider");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);

        using var optedOut = ComposeWorkspace(optedIn: false);
        await SeedWorkspaceAsync(optedOut, editLocally: false);
        (await WorkspaceAsync(optedOut, "ids")).ShouldBe(ids, "ids are structure and must not depend on the overlay");
        Records(await WorkspaceAsync(optedOut, "json")).Keys.Order().ToArray().ShouldBe(records.Keys.Order().ToArray());
    }

    [Fact]
    public async Task OptedIn_SprintLayoutGroupsByTheMirrorAssignee_WhileTheRecordShowsTheDurableAssignee()
    {
        using var services = ComposeWorkspace(optedIn: true);
        await SeedWorkspaceAsync(services, editLocally: true);
        var before = DurableRows();

        var json = await WorkspaceAsync(services, "json", sprintLayout: true);

        using var doc = JsonDocument.Parse(json);
        var groups = FindProperty(doc.RootElement, "sprintByAssignee")!.Value;
        groups.EnumerateObject().Select(g => g.Name).ShouldBe([IdentityStubs.DefaultDisplayName],
            "grouping is structure: the durable reassignment must not open an 'Other Person' group");
        var grouped = Records(groups.GetProperty(IdentityStubs.DefaultDisplayName).GetRawText());
        grouped.Keys.Order().ToArray().ShouldBe(new long[] { 42, 44 });
        Cell(grouped[42], "assignedTo").ShouldBe("Other Person");
        Cell(grouped[42], "title").ShouldBe("Local member");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);
    }

    [Fact]
    public async Task OptedIn_HumanFlatWorkspace_ShowsDurableCellsInsideTheMirrorBucketAndFooter()
    {
        using var services = ComposeWorkspace(optedIn: true);
        await SeedWorkspaceAsync(services, editLocally: true);
        var before = DurableRows();

        var output = await WorkspaceAsync(services, "human", flat: true);

        output.ShouldContain("Local member");
        output.ShouldNotContain("Remote member");
        output.ShouldContain("Closed");
        // Documented tradeoff: the edited-to-Closed row stays in the mirror's In Progress bucket
        // and the footer still counts it as not done.
        output.ShouldContain("In Progress");
        output.ShouldNotContain("Completed");
        output.ShouldContain("0/2");
        output.ShouldNotContain("Local outsider");
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);
    }

    [Theory]
    [InlineData("json", false, false)]
    [InlineData("json", true, false)]
    [InlineData("human", false, true)]
    [InlineData("human", true, false)]
    public async Task OptedOut_WorkspaceIsByteIdenticalToTheUneditedMirror_AndNeverResolvesTheProvider(string format, bool sprintLayout, bool flat)
    {
        using var optedOut = ComposeWorkspace(optedIn: false);
        await SeedWorkspaceAsync(optedOut, editLocally: false);
        var optedOutOutput = await WorkspaceAsync(optedOut, format, sprintLayout, flat);

        optedOutOutput.ShouldContain("Remote member");
        _provider.Calls.ShouldBe(0);
        DurableRows().ShouldBeEmpty();

        using var optedInUnedited = ComposeWorkspace(optedIn: true);
        await SeedWorkspaceAsync(optedInUnedited, editLocally: false);
        (await WorkspaceAsync(optedInUnedited, format, sprintLayout, flat)).ShouldBe(optedOutOutput);
    }

    [Fact]
    public async Task OptedIn_NullScope_WorkspaceFallsBackToTheMirror_WithoutProjecting()
    {
        _provider.ResolvedScope = null;
        using var services = ComposeWorkspace(optedIn: true);
        await SeedWorkspaceAsync(services, editLocally: false);

        var records = Records(await WorkspaceAsync(services, "json"));

        records.Keys.Order().ToArray().ShouldBe(new long[] { 42, 44 });
        Cell(records[42], "title").ShouldBe("Remote member");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBeEmpty();
    }

    [Fact]
    public async Task OptedIn_LiveWorkspaceDrawsDurableCells_WhileMembershipStaysOnTheMirror()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 200;
        using var services = ComposeWorkspace(optedIn: true, liveConsole: console);
        await SeedWorkspaceAsync(services, editLocally: true);
        var before = DurableRows();

        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, stderr) = await StderrCapture.RunAsync(() => command.ExecuteAsync("human", flat: true));

        exit.ShouldBe(0, stderr);
        console.Output.ShouldContain("Local member");
        console.Output.ShouldNotContain("Remote member");
        console.Output.ShouldContain("Closed");
        console.Output.ShouldContain("Untouched member");
        console.Output.ShouldNotContain("outsider");
        // Buckets and footer stay on the mirror: the edited-to-Closed row is still In Progress.
        console.Output.ShouldContain("In Progress");
        console.Output.ShouldNotContain("Completed");
        console.Output.ShouldContain("0/2");
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);
    }

    [Fact]
    public async Task OptedIn_LiveContextCaptionShowsDurableValues_WhileTheContextStaysOutOfTheRows()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 200;
        using var services = ComposeWorkspace(optedIn: true, liveConsole: console);
        await SeedWorkspaceAsync(services, editLocally: true);
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(43);
        var before = DurableRows();

        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, stderr) = await StderrCapture.RunAsync(() => command.ExecuteAsync("human", flat: true));

        exit.ShouldBe(0, stderr);
        console.Output.ShouldContain("Active: #43 Local outsider");
        console.Output.ShouldNotContain("Remote outsider");
        // Membership stays on the mirror: #43 is only the caption, never a Sprint 1 row.
        (console.Output.Split("Local outsider").Length - 1).ShouldBe(1);
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);
    }

    [Fact]
    public async Task OptedIn_IncludeBrowserJsonStaysRawObservedEvidence()
    {
        var binding = Substitute.For<IConnectionBindingService>();
        // The browser document takes membership from the Bench, which resolves the sprint through the
        // cached calendar rather than the iteration service.
        var calendar = Substitute.For<IIterationCalendar>();
        calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns([IterationPath.Parse(@"Project\Sprint 1").Value]);
        binding.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(
                new IdentityBinding("binding-1", "connection-1", "identity-1", 1),
                new AuthenticationIdentity("identity-1", "Work", "tenant", "object", "issuer", "host", "credential", "user"),
                "C:/checkout", "checkout-binding-pin", 1,
                new ConnectionOperationSnapshot("org", "Project", "Team", "fingerprint", 1, "portable", "preferences", "unicode", "portable")));
        using var services = ComposeWorkspace(optedIn: true, replace: s =>
        {
            s.AddSingleton(binding);
            s.AddSingleton(calendar);
        });
        await SeedWorkspaceAsync(services, editLocally: true);
        var before = DurableRows();

        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync("json", view: "tree", includeBrowser: true));

        exit.ShouldBe(0, output);
        output.ShouldContain("Remote member");
        output.ShouldNotContain("Local member");
        output.ShouldNotContain("Other Person");
        DurableRows().ShouldBe(before);
        await AssertMirrorUntouchedAsync(services);
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                    return property.Value;
                if (FindProperty(property.Value, name) is { } nested)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (FindProperty(child, name) is { } nested)
                    return nested;
        }
        return null;
    }
}
