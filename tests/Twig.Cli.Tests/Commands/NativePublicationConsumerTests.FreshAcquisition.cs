using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeAdoAcquisitionResolvesFreshSourceOnceAtEachEntry()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        fixture.Bindings.ResolveCount = 0;
        var acquired = await handle.NativeAdoAccess!.AcquireAsync(handle.Workspace.Connection, default);
        acquired.IsSuccess.ShouldBeTrue(acquired.IsSuccess ? "" : acquired.Error.Message);
        using var session = acquired.Value;
        fixture.Bindings.ResolveCount.ShouldBe(1, "One current resolution must run both real guard and clean-core expected-origin checks.");
        fixture.Bindings.TokenCount.ShouldBeGreaterThan(0);
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("default-revision")]
    [InlineData("binding")]
    [InlineData("pin")]
    [InlineData("principal")]
    [InlineData("method")]
    [InlineData("configuration")]
    [InlineData("generation")]
    public async Task NativeAdoAcquisitionWithinLiveAncestorStillRefusesChangedSource(string damage)
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        using var ancestor = await ((IConnectionOperationGuard)fixture.Authentication).AcquireOperationAsync();
        var original = ((IAdmittedConnectionOperation)ancestor).GetAuthority(fixture.Bindings).ShouldNotBeNull();
        fixture.Bindings.ResolveCount = 0;
        var first = await handle.NativeAdoAccess!.AcquireAsync(handle.Workspace.Connection, default);
        first.IsSuccess.ShouldBeTrue();
        first.Value.Dispose();
        fixture.Bindings.ResolveCount.ShouldBe(1);
        var tokenCount = fixture.Bindings.TokenCount;
        var attachmentPath = Path.Combine(fixture.Paths.TwigDir, "attachment.json");
        var attachment = await File.ReadAllTextAsync(attachmentPath);
        if (damage == "attachment")
        {
            var changed = JsonNode.Parse(attachment)!;
            changed["revision"] = changed["revision"]!.GetValue<long>() + 1;
            await File.WriteAllTextAsync(attachmentPath, changed.ToJsonString());
        }
        else if (damage == "pin")
        {
            var changed = JsonSerializer.Deserialize(attachment, TwigJsonContext.Default.AttachmentDocument)!;
            await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(changed with { BindingPin = original.Binding.BindingId },
                TwigJsonContext.Default.AttachmentDocument));
            var observed = await _bindings.ResolveAsync(fixture.Configuration, fixture.Paths);
            observed.SelectionSource.ShouldBe("checkout-binding-pin");
            observed.Binding.ShouldBe(original.Binding);
        }
        else if (damage is "principal" or "method")
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = _bindings.RegistryPath, Pooling = false }.ToString());
            await database.OpenAsync();
            using var command = database.CreateCommand();
            command.CommandText = damage == "method" ? "DELETE FROM pat_principals WHERE identity_id=$id;"
                : "UPDATE pat_principals SET principal_id=$principal WHERE identity_id=$id;";
            command.Parameters.AddWithValue("$id", original.Identity.IdentityId);
            if (damage == "principal") command.Parameters.AddWithValue("$principal", Sibling);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
            var observed = await _bindings.ResolveAsync(fixture.Configuration, fixture.Paths);
            if (damage == "method") observed.Identity.Method.ShouldNotBe(original.Identity.Method);
            else observed.Identity.AdoPrincipalId.ShouldBe(Sibling);
        }
        else if (damage == "configuration")
        {
            fixture.Configuration.Team = "Changed fixture team";
            await fixture.Configuration.SaveSplitAsync(fixture.Paths);
        }
        else if (damage == "generation")
        {
            original.StorageGeneration.ShouldBeNull();
            var generation = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(fixture.Paths.TwigDir, "cache"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Paths.TwigDir, "cache", MirrorAdmission.MarkerFile),
                JsonSerializer.Serialize(new MirrorAdmissionDocument(1, _bindings.RegistryPath,
                    original.Operation.WorktreeFingerprint, original.Binding.ConnectionRef, original.Binding.BindingId,
                    generation, "admitted-" + generation + ".db"), TwigJsonContext.Default.MirrorAdmissionDocument));
        }
        else
        {
            var selected = damage == "binding"
                ? await _bindings.CreateBindingAsync(fixture.Configuration.Organization, fixture.Configuration.Project, "sibling", false)
                : original.Binding;
            SwapSelection(fixture.Configuration, selected.BindingId);
        }
        var refused = await handle.NativeAdoAccess.AcquireAsync(handle.Workspace.Connection, default);
        refused.IsSuccess.ShouldBeFalse();
        fixture.Bindings.TokenCount.ShouldBe(tokenCount, "A failed fresh entry cannot release a credential.");
        ((IAdmittedConnectionOperation)ancestor).GetAuthority(fixture.Bindings).ShouldBe(original);
        _transport.PatchAttempts.ShouldBe(0);
        if (damage == "attachment")
        {
            await File.WriteAllTextAsync(attachmentPath, attachment);
            var again = await handle.NativeAdoAccess.AcquireAsync(handle.Workspace.Connection, default);
            again.IsSuccess.ShouldBeTrue();
            again.Value.Dispose();
        }
        using var released = ConnectionOperationGate.Acquire(fixture.Paths.RepoRoot);
    }

    [Fact]
    public async Task NativeAdoFreshChildRetainsPhysicalLeaseAfterAncestorDisposal()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        using var ancestor = await ((IConnectionOperationGuard)fixture.Authentication).AcquireOperationAsync();
        var acquired = await handle.NativeAdoAccess!.AcquireAsync(handle.Workspace.Connection, default);
        acquired.IsSuccess.ShouldBeTrue();
        using var session = acquired.Value;
        ancestor.Dispose();
        ((IAdmittedConnectionOperation)ancestor).GetAuthority(fixture.Bindings).ShouldBeNull();
        Should.Throw<ConnectionBindingResolutionException>(() =>
        {
            using var unexpected = ConnectionOperationGate.Acquire(fixture.Paths.RepoRoot, exclusive: true);
        });
        session.Dispose();
        using var released = ConnectionOperationGate.Acquire(fixture.Paths.RepoRoot, exclusive: true);
    }

    [Fact]
    public async Task NativeAdoAcquisitionWrongResolverCannotBorrowAuthority()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        var different = new CountingNativeBindings(_bindings);
        var origin = await fixture.Bindings.ResolveAsync(fixture.Configuration, fixture.Paths);
        var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(handle.Document.DatabasePath, default,
            handle.Document.DatabaseInstance)).Value;
        var access = new CleanCoreAdoAccess(fixture.Authentication, different, fixture.Configuration, fixture.Paths,
            origin, handle.Workspace.Connection, handle.Document.PrincipalId, store);
        var tokens = fixture.Bindings.TokenCount;
        (await access.AcquireAsync(handle.Workspace.Connection, default)).IsSuccess.ShouldBeFalse();
        fixture.Bindings.TokenCount.ShouldBe(tokens);
        using var released = ConnectionOperationGate.Acquire(fixture.Paths.RepoRoot, exclusive: true);
    }

    [Fact]
    public async Task NativeAdoAcquisitionCancellationReleasesItsLease()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        using var cancellation = new CancellationTokenSource();
        fixture.Bindings.BeforeResolve = () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        var tokens = fixture.Bindings.TokenCount;
        await Should.ThrowAsync<OperationCanceledException>(() => handle.NativeAdoAccess!
            .AcquireAsync(handle.Workspace.Connection, cancellation.Token));
        fixture.Bindings.TokenCount.ShouldBe(tokens);
        fixture.Bindings.BeforeResolve = null;
        using (ConnectionOperationGate.Acquire(fixture.Paths.RepoRoot, exclusive: true)) { }
        var acquired = await handle.NativeAdoAccess!.AcquireAsync(handle.Workspace.Connection, default);
        acquired.IsSuccess.ShouldBeTrue();
        acquired.Value.Dispose();
    }

    [Fact]
    public async Task NativeAdoAcquisitionDifferentPreferenceContextKeepsFullValidation()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        var paths = new TwigPaths(fixture.Paths.TwigDir, fixture.Paths.ConfigPath, fixture.Paths.DbPath,
            fixture.Paths.StartDir, Path.Combine(Home, "other-display.json"));
        var origin = await fixture.Bindings.ResolveAsync(fixture.Configuration, paths);
        var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(handle.Document.DatabasePath, default,
            handle.Document.DatabaseInstance)).Value;
        var access = new CleanCoreAdoAccess(fixture.Authentication, fixture.Bindings, fixture.Configuration, paths,
            origin, handle.Workspace.Connection, handle.Document.PrincipalId, store);
        fixture.Bindings.ResolveCount = 0;
        var acquired = await access.AcquireAsync(handle.Workspace.Connection, default);
        acquired.IsSuccess.ShouldBeTrue();
        acquired.Value.Dispose();
        fixture.Bindings.ResolveCount.ShouldBe(2, "Different source contexts cannot share a current observation.");
    }

    [Fact]
    public async Task NativeAdoApplyingAdmissionStillRevalidatesAfterDurableAttempt()
    {
        var fixture = await FreshAcquisitionFixtureAsync();
        using var handle = fixture.Handle;
        var host = fixture.Host;
        using var editor = (await NativeItemEditor.OpenAsync(host.Root, host.Bench, host.Stamp, services =>
        {
            services.AddSingleton<IConnectionBindingService>(fixture.Bindings);
            services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        }, default)).Value;
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var link = new CreationLink(other, "Native.DependencyForward", true);
        form.Choices.ShouldContain(link);
        (await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [link])).IsSuccess.ShouldBeTrue();
        var capture = (await UserBenchStore.ForRegistry(host.Registry).ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
        var injected = false;
        fixture.Bindings.BeforeResolve = async () =>
        {
            var snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
            if (!snapshot.Journal.Any(row => row.Status == OpStatus.Applying)) return;
            snapshot.Journal.ShouldHaveSingleItem().Attempt.ShouldNotBe(Guid.Empty);
            _transport.PatchAttempts.ShouldBe(0);
            fixture.Bindings.BeforeResolve = null;
            injected = true;
            var path = Path.Combine(fixture.Paths.TwigDir, "attachment.json");
            var attachment = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            attachment["revision"] = attachment["revision"]!.GetValue<long>() + 1;
            await File.WriteAllTextAsync(path, attachment.ToJsonString());
        };
        var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [link]);
        injected.ShouldBeTrue("The mutation must occur after the real Applying journal commit, not during entry.");
        (saved.IsSuccess && saved.Value.Verified).ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(0);
        var final = (await handle.Workspace.SnapshotAsync(default)).Value;
        final.Drafts.ShouldHaveSingleItem();
        final.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Failed);
        (await UserBenchStore.ForRegistry(host.Registry).ReadEditorCapturesAsync(default)).Value
            .ShouldHaveSingleItem().Settled.ShouldBeFalse();
        capture.Id.ShouldNotBe(Guid.Empty);
    }

    private async Task<FreshAcquisitionFixture> FreshAcquisitionFixtureAsync()
    {
        var host = await CreateHumanHostAsync();
        var paths = TwigPaths.BuildPaths(Path.Combine(host.Root, ".twig"), new TwigConfiguration(), host.Root);
        var configuration = await TwigConfiguration.LoadSplitAsync(paths);
        var bindings = new CountingNativeBindings(_bindings);
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddCoreWorkspaceServices(configuration, paths);
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
        var opened = await factory.OpenAsync(default);
        opened.IsSuccess.ShouldBeTrue();
        opened.Value.Workspace.Connection.Provider.Value.ShouldBe("ado");
        (await factory.AdmitSourceAsync(opened.Value, default)).IsSuccess.ShouldBeTrue();
        var authentication = runtime.GetRequiredService<IAuthenticationProvider>();
        authentication.ShouldBeOfType<DeferredBoundAuthenticationProvider>();
        var original = await bindings.ResolveAsync(configuration, paths);
        original.Identity.Method.ShouldBe("pat");
        original.Identity.AdoPrincipalId.ShouldBe(Actor);
        return new(host, paths, configuration, bindings, authentication, opened.Value);
    }

    private sealed record FreshAcquisitionFixture(
        (string Root, string Registry, Guid Bench, NativeSourceStamp Stamp) Host,
        TwigPaths Paths, TwigConfiguration Configuration, CountingNativeBindings Bindings,
        IAuthenticationProvider Authentication, CoreWorkspaceHandle Handle);

    private sealed class CountingNativeBindings(IConnectionBindingService inner) : IConnectionBindingService
    {
        internal int ResolveCount;
        internal int TokenCount;
        internal Func<Task>? BeforeResolve;
        public string RegistryPath => inner.RegistryPath;
        public async Task<ResolvedConnectionBinding> ResolveAsync(TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ResolveCount);
            if (BeforeResolve is { } before) await before();
            return await inner.ResolveAsync(configuration, paths, ct);
        }
        public IAuthenticationProvider CreateAuthenticationProvider(ResolvedConnectionBinding binding)
            => new MigrationBoundAuthenticationProvider(new FixtureCredential(this, binding.Identity), binding, RegistryPath, this);
        public Task<AuthenticationIdentity> RegisterAadIdentityAsync(string name, TwigRefreshTokenStoreEntry credential, CancellationToken ct = default)
            => inner.RegisterAadIdentityAsync(name, credential, ct);
        public Task<AuthenticationIdentity> RegisterPatIdentityAsync(string name, string organization, string pat, CancellationToken ct = default)
            => inner.RegisterPatIdentityAsync(name, organization, pat, ct);
        public Task ClearIdentityAccessCacheAsync(string name, CancellationToken ct = default) => inner.ClearIdentityAccessCacheAsync(name, ct);
        public Task VerifyIdentityCredentialAsync(string name, string organization, CancellationToken ct = default)
            => inner.VerifyIdentityCredentialAsync(name, organization, ct);
        public Task<IReadOnlyList<AuthenticationIdentity>> ListIdentitiesAsync(CancellationToken ct = default) => inner.ListIdentitiesAsync(ct);
        public Task<IdentityBinding> CreateBindingAsync(string organization, string project, string identityName, bool makeDefault, CancellationToken ct = default)
            => inner.CreateBindingAsync(organization, project, identityName, makeDefault, ct);
        public Task<IReadOnlyList<IdentityBinding>> ListBindingsAsync(string organization, string project, CancellationToken ct = default)
            => inner.ListBindingsAsync(organization, project, ct);
        public Task<IAuthenticationProvider> CreateBootstrapProviderAsync(TwigConfiguration configuration, CancellationToken ct = default, string? bindingId = null)
            => inner.CreateBootstrapProviderAsync(configuration, ct, bindingId);

        private sealed class FixtureCredential(CountingNativeBindings owner, AuthenticationIdentity identity)
            : IAuthenticationProvider, IBoundAuthenticationMetadata
        {
            public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref owner.TokenCount);
                var pat = identity.AdoPrincipalId switch
                {
                    Actor => ActorPat,
                    Sibling => SiblingPat,
                    _ => throw new InvalidOperationException("Unknown fixture credential identity."),
                };
                return Task.FromResult("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)));
            }
            public Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(identity);
            }
            public void InvalidateToken() { }
        }
    }
}
