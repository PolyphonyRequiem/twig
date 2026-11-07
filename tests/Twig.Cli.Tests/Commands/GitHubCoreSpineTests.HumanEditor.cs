using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeGitHubHumanEditorSavesScalarsAndExplicitMarkdownClearThroughCore()
    {
        using var fixture = new HumanEditorFixture();
        var host = await fixture.InitializeAsync();
        host.Stamp.Connection.Provider.Value.ShouldBe("github");
        fixture.Writes.ShouldBe(0);
        var opened = await fixture.OpenAsync();
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var editor = opened.Value;
        var form = (await editor.SelectAsync(fixture.Key)).Value;
        form.StateField.ShouldBe(GitHubProvider.StateField);
        form.Evaluation.States.ShouldContain("CLOSED");
        form.Evaluation.Fields.Single(field => field.Definition.Reference == GitHubProvider.Assignees).ReadOnly.ShouldBeTrue();
        var fields = FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("Native human title"))
            .Set(GitHubProvider.Body, FieldValue.RichText("**native Markdown**\n\n<body>", FieldKind.Markdown))
            .Set(form.StateField, FieldValue.String("CLOSED"));
        var saved = await editor.SaveAsync(form.Ticket, fields);
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
        saved.Value.DraftRetained.ShouldBeFalse();
        fixture.Writes.ShouldBe(1);
        var snapshot = await fixture.SnapshotAsync();
        snapshot.Drafts.ShouldBeEmpty();
        snapshot.Items.Single().Fields[GitHubProvider.Body].ShouldBe(fields[GitHubProvider.Body]);
        snapshot.Proposals.Single().Operations.Single().Concurrency.ShouldBe(ConcurrencyMode.ObservedNonAtomic);
        snapshot.Proposals.Single().Operations.Single().Fields.Count.ShouldBe(3);
        form = (await editor.SelectAsync(fixture.Key)).Value;
        (await editor.SaveAsync(form.Ticket, FieldValues.Empty.Set(GitHubProvider.Body, FieldValue.Null)))
            .IsSuccess.ShouldBeFalse("a body clear must explicitly carry Markdown, not Null");
        fixture.Writes.ShouldBe(1);
        saved = await editor.SaveAsync(form.Ticket,
            FieldValues.Empty.Set(GitHubProvider.Body, FieldValue.RichText("", FieldKind.Markdown)));
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.Verified.ShouldBeTrue();
        fixture.Writes.ShouldBe(2);
        (await fixture.SnapshotAsync()).Items.Single().Fields[GitHubProvider.Body].ShouldBe(FieldValue.RichText("", FieldKind.Markdown));
    }

    [Fact]
    public async Task NativeGitHubHumanPlanStagesScalarsAndNormalSaveNeverFlushesPlanDebt()
    {
        using var fixture = new HumanEditorFixture();
        await fixture.InitializeAsync();
        await fixture.SetPlanAsync(true);
        using (var plan = (await fixture.OpenAsync()).Value)
        {
            plan.PlanMode.ShouldBeTrue();
            plan.StoreInstance.ShouldNotBe(fixture.Host.Stamp.StoreInstance);
            var form = (await plan.SelectAsync(fixture.Key)).Value;
            var staged = await plan.StageAsync(form.Ticket, FieldValues.Empty
                .Set(GitHubProvider.Title, FieldValue.String("Plan-only title"))
                .Set(GitHubProvider.Body, FieldValue.RichText("**Plan-only**", FieldKind.Markdown))
                .Set(form.StateField, FieldValue.String("CLOSED")));
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            (await plan.SaveAsync(form.Ticket, FieldValues.Empty)).Error.Code.ShouldBe(ErrorCode.Conflict);
            (await plan.CreateSeedAsync(form.Type)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await plan.SelectAsync(ItemKey.Local(Guid.NewGuid()))).Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await fixture.SnapshotAsync()).Drafts.ShouldBeEmpty();
            fixture.Writes.ShouldBe(0);
        }
        await fixture.SetPlanAsync(false);
        using (var normal = (await fixture.OpenAsync()).Value)
        {
            var form = (await normal.SelectAsync(fixture.Key)).Value;
            form.Values[GitHubProvider.Title].Text.ShouldBe("original");
            var result = await normal.SaveAsync(form.Ticket,
                FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("Normal-only title")));
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
            result.Value.Verified.ShouldBeTrue();
            fixture.Writes.ShouldBe(1);
        }
        await fixture.SetPlanAsync(true);
        using var reopened = (await fixture.OpenAsync()).Value;
        var debt = (await reopened.ReadPlanDraftsAsync()).Value.Single();
        debt.Changes[GitHubProvider.Title].Text.ShouldBe("Plan-only title");
        debt.Changes[GitHubProvider.Body].Kind.ShouldBe(FieldKind.Markdown);
        debt.Changes[GitHubProvider.StateField].Text.ShouldBe("CLOSED");
        fixture.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("repository")]
    [InlineData("observation")]
    [InlineData("mode")]
    [InlineData("draft")]
    [InlineData("schema")]
    public async Task NativeGitHubHumanEditorFreshGuardsRefuseBeforeMutation(string defect)
    {
        using var fixture = new HumanEditorFixture();
        await fixture.InitializeAsync();
        using var editor = (await fixture.OpenAsync()).Value;
        var form = (await editor.SelectAsync(fixture.Key)).Value;
        if (defect == "mode") await fixture.SetPlanAsync(true);
        else if (defect == "draft") await fixture.StageNormalAsync(GitHubProvider.Title, FieldValue.String("CLI debt"));
        else if (defect == "observation") fixture.ExternalTitle("Concurrent native title");
        else fixture.Defect = defect;
        var result = await editor.SaveAsync(form.Ticket, FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("Human intent")));
        (result.IsSuccess && result.Value.Verified).ShouldBeFalse();
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGitHubHumanEditorRefusesCollectionsAndRetainsProtectedIntent(bool plan)
    {
        using var fixture = new HumanEditorFixture();
        await fixture.InitializeAsync();
        await fixture.SetPlanAsync(plan);
        using var editor = (await fixture.OpenAsync()).Value;
        var form = (await editor.SelectAsync(fixture.Key)).Value;
        var changes = FieldValues.Empty.Set(GitHubProvider.Assignees, FieldValue.Strings(["U_actor"]));
        (await editor.EvaluateAsync(form.Ticket, changes)).IsSuccess.ShouldBeFalse();
        var result = plan ? (await editor.StageAsync(form.Ticket, changes)).IsSuccess
            : (await editor.SaveAsync(form.Ticket, changes)).IsSuccess;
        result.ShouldBeFalse();
        fixture.Writes.ShouldBe(0);
        (await editor.ReadPlanDraftsAsync()).IsSuccess.ShouldBe(plan);
        await fixture.Native.UseEditor(plan, async (native, _, _) =>
        {
            var selected = (await native.SelectAsync(fixture.Key, default)).Value;
            var staged = await native.StageAsync(selected, changes, null, null, default);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            using var guarded = (await NativeItemEditor.OpenAsync(fixture.Host.Root, native.BenchId,
                native.Origin, fixture.Configure, default)).Value;
            var refused = await guarded.SelectAsync(fixture.Key);
            refused.IsSuccess.ShouldBeFalse();
            refused.Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await native.Workspace.SnapshotAsync(default)).Value.Drafts.Single().Changes.ShouldBe(changes);
        });
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubHumanEditorUncertainSaveKeepsOriginalReceiptAndNeverRetries()
    {
        using var fixture = new HumanEditorFixture();
        await fixture.InitializeAsync();
        using var editor = (await fixture.OpenAsync()).Value;
        var form = (await editor.SelectAsync(fixture.Key)).Value;
        fixture.Native.Http.LoseAcknowledgement = true;
        var changes = FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("Uncertain intent"));
        var first = await editor.SaveAsync(form.Ticket, changes);
        (first.IsSuccess && first.Value.Verified).ShouldBeFalse();
        fixture.Writes.ShouldBe(1);
        var before = await fixture.SnapshotAsync();
        before.Drafts.ShouldHaveSingleItem();
        before.Journal.ShouldNotBeEmpty();
        var second = await editor.SaveAsync(form.Ticket, changes);
        (second.IsSuccess && second.Value.Verified).ShouldBeFalse();
        fixture.Writes.ShouldBe(1);
        (await fixture.SnapshotAsync()).Journal.ShouldBe(before.Journal);
    }

    public sealed class HumanEditorFixture : IDisposable
    {
        internal Fixture Native { get; } = new();
        public (string Root, string Registry, Guid Bench, NativeSourceStamp Stamp) Host { get; private set; }
        public ItemKey Key => ItemKey.Remote(Host.Stamp.Connection.Provider, Host.Stamp.Connection.ResourceScope, "I_opaque");
        public int Writes => Native.Http.Writes;
        public string? Defect { set => Native.Http.Defect = value; }
        public async Task<(string Root, string Registry, Guid Bench, NativeSourceStamp Stamp)> InitializeAsync()
        {
            await Native.Initialize();
            Native.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, Native.Error);
            var source = (await Native.ReadSource(schema: true)).Value;
            var user = UserBenchStore.ForRegistry(Native._bindings.RegistryPath);
            var bench = (await user.SaveAsync(new(Guid.NewGuid(), "GitHub editor fixture",
                [new(SelectorKind.Pin, KeyFor(source.Source), connection: source.Source.Connection)]), 0)).Value;
            Host = (Native.Root, Native._bindings.RegistryPath, bench.Id, source.Source);
            (await SelectBenchAsync(null, bench.Id, default)).IsSuccess.ShouldBeTrue();
            Writes.ShouldBe(0);
            return Host;
        }
        private static ItemKey KeyFor(NativeSourceStamp source)
            => ItemKey.Remote(source.Connection.Provider, source.Connection.ResourceScope, "I_opaque");
        internal void Configure(IServiceCollection services)
        {
            services.AddSingleton<IConnectionBindingService>(Native._bindings);
            services.AddSingleton(Native._processes);
            services.AddSingleton(new CoreTransportOverride(Native.Http.NewHandle));
        }
        public Task<Result<NativeItemEditor>> OpenAsync(CancellationToken ct = default)
            => NativeItemEditor.OpenAsync(Host.Root, Host.Bench, Host.Stamp, Configure, ct);
        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool schema, CancellationToken ct)
            => NativeSourceReader.ReadAsync(root, expected, schema, Configure, ct);
        public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
            => NativeSourceReader.ReadOnlineAsync(root, expected, Configure, ct);
        public Task<Result<NativeSourceObservation>> SelectBenchAsync(Guid? previous, Guid? next, CancellationToken ct)
            => NativeSourceReader.SelectBenchAsync(Host.Root, Host.Stamp, previous, next, Configure, ct);
        public Task<Result<NativePlanReview>> ReviewAsync(ImmutableArray<NativePlanSource> sources, CancellationToken ct)
            => NativePlanReview.ReviewAsync(Host.Root, Host.Bench, sources, Configure, ct);
        public Task<Result<NativePlanReview>> OpenReviewAsync(string digest, CancellationToken ct)
            => NativePlanReview.OpenAsync(Host.Root, digest, Configure, ct);
        public async Task SetPlanAsync(bool enabled)
        {
            var user = UserBenchStore.ForRegistry(Host.Registry);
            var status = (await user.ReadPlanStatusAsync(Host.Bench)).Value;
            if (status.Enabled != enabled)
                (await user.ChangePlanModeAsync(status.UserInstance, Host.Bench, status.BenchRevision, status.Revision, enabled)).IsSuccess.ShouldBeTrue();
        }
        public void ExternalTitle(string title)
        {
            Native.Http.Issue["title"] = title;
            Native.Http.Issue["updatedAt"] = "2026-11-06T12:00:00Z";
        }
        public Task StageNormalAsync(string field, FieldValue value) => Native.Stage(field, value);
        public Task<StoreSnapshot> SnapshotAsync() => Native.Snapshot();
        public void Dispose() => Native.Dispose();
    }
}
