using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class FreshAdoInitializationTests : IDisposable
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Actor = "22222222-2222-2222-2222-222222222222";
    private const string Sibling = "33333333-3333-3333-3333-333333333333";
    private const string Principal = "44444444-4444-4444-4444-444444444444";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-fresh-ado-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _requests = [];
    private ConnectionBindingService? _bindings;
    private string Root => Path.Combine(_temp, "repo");
    private string TwigDir => Path.Combine(Root, ".twig");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineFreshSetupCreatesCurrentAdmissionWithoutAnExistingMirror(bool customStore)
    {
        var (bindings, selected, _) = await ArrangeAsync();
        var database = customStore ? Path.Combine(_temp, "explicit.db") : Path.Combine(TwigDir, "core.db");
        if (customStore) await File.WriteAllTextAsync(Path.Combine(_temp, "display.json"), """{"icons":"nerd"}""");
        var observedFreshBoundary = false;
        var transport = Transport(async () =>
        {
            observedFreshBoundary = true;
            File.Exists(database).ShouldBeFalse();
            File.Exists(Path.Combine(TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeFalse();
            File.Exists(Path.Combine(TwigDir, "cache", "admission.json")).ShouldBeFalse();
            var paths = new TwigPaths(TwigDir, Path.Combine(TwigDir, "config"), database, Root);
            var config = await TwigConfiguration.LoadSplitAsync(paths);
            var refused = await Should.ThrowAsync<InvalidOperationException>(() => bindings.ResolveAsync(config, paths));
            refused.Message.ShouldContain("activation-required");
            Task blocked;
            using (ExecutionContext.SuppressFlow())
                blocked = Task.Run(() => Should.Throw<ConnectionBindingResolutionException>(
                    () => { using var other = ConnectionOperationGate.Acquire(Root, exclusive: true); }));
            await blocked;
        });

        var initialized = await NativeWorkspaceSetup.InitializeAsync(Arguments(customStore ? database : null),
            () => bindings, new NativeSetupEffects(), default, transport);

        initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
        observedFreshBoundary.ShouldBeTrue("the real fresh metadata request must run before any store or marker exists");
        _requests.Count.ShouldBe(2);
        var paths = TwigPaths.BuildPaths(TwigDir, Root);
        paths.DbPath.ShouldBe(database);
        var config = await TwigConfiguration.LoadSplitAsync(paths);
        var origin = await bindings.ResolveAsync(config, paths);
        origin.Binding.ShouldBe(selected);
        origin.SelectionSource.ShouldBe("connection-default-binding");
        origin.StorageGeneration.ShouldBe("core:" + initialized.Value.Document.DatabaseInstance.ToString("N"));
        origin.Identity.ObjectId.ShouldBe(Actor);
        using var authentication = (AdmittedBoundAuthenticationProvider)bindings.CreateAuthenticationProvider(origin);
        (await authentication.GetAccessTokenAsync()).ShouldBe(Token("actor"));
        var opened = await new CoreWorkspaceFactory(config, paths, bindings, authentication, transport).OpenAsync(default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var handle = opened.Value;
        handle.Source.ShouldBe(initialized.Value.Source);
        var store = await SqliteStore.OpenAsync(database, expectedInstance: initialized.Value.Document.DatabaseInstance);
        store.IsSuccess.ShouldBeTrue();
        var snapshot = await store.Value.ReadAsync(initialized.Value.Document.Connection, default);
        snapshot.IsSuccess.ShouldBeTrue();
        snapshot.Value.Version.ShouldBe(0);
        snapshot.Value.Drafts.ShouldBeEmpty();
        snapshot.Value.Journal.ShouldBeEmpty();
        File.Exists(Path.Combine(TwigDir, "cache", "admission.json")).ShouldBeFalse();
        using var released = ConnectionOperationGate.Acquire(Root, exclusive: true);
    }

    [Fact]
    public async Task FreshSetupRefusesChangedDefaultActorBeforeCreatingAStore()
    {
        var (bindings, selected, sibling) = await ArrangeAsync();
        var transport = Transport(async () =>
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = bindings.RegistryPath, Pooling = false }.ToString());
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE connection_defaults SET binding_id=$next,revision=revision+1 WHERE connection_ref=$ref AND binding_id=$prior;";
            command.Parameters.AddWithValue("$next", sibling.BindingId);
            command.Parameters.AddWithValue("$prior", selected.BindingId);
            command.Parameters.AddWithValue("$ref", selected.ConnectionRef);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        });

        var initialized = await NativeWorkspaceSetup.InitializeAsync(Arguments(), () => bindings,
            new NativeSetupEffects(), default, transport);

        _requests.Count.ShouldBe(2, "reach the actual metadata response before testing selector revalidation");
        initialized.IsSuccess.ShouldBeFalse();
        File.Exists(Path.Combine(TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeFalse();
        File.Exists(Path.Combine(TwigDir, "core.db")).ShouldBeFalse();
        Directory.Exists(TwigDir).ShouldBeTrue("partial setup evidence is preserved, not reset or adopted");
        using var released = ConnectionOperationGate.Acquire(Root, exclusive: true);
    }

    [Fact]
    public async Task CurrentAdmissionRefusesAValidButUnregisteredReplacementStore()
    {
        var (bindings, _, _) = await ArrangeAsync();
        var initialized = await NativeWorkspaceSetup.InitializeAsync(Arguments(), () => bindings,
            new NativeSetupEffects(), default, Transport());
        initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
        var original = initialized.Value.Document;
        var replacement = await SqliteStore.CreateAsync(Path.Combine(TwigDir, "replacement.db"));
        replacement.IsSuccess.ShouldBeTrue();
        (await replacement.Value.PinWorkspaceAsync(original.Connection, default)).IsSuccess.ShouldBeTrue();
        await File.WriteAllTextAsync(Path.Combine(TwigDir, CoreWorkspaceFactory.FileName),
            JsonSerializer.Serialize(original with
            {
                DatabaseInstance = replacement.Value.InstanceId,
                DatabasePath = replacement.Value.DatabasePath,
            }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        var paths = TwigPaths.BuildPaths(TwigDir, Root);
        var config = await TwigConfiguration.LoadSplitAsync(paths);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => bindings.ResolveAsync(config, paths));

        refused.Message.ShouldContain("activation-store-unregistered");
        (await SqliteStore.OpenAsync(original.DatabasePath, expectedInstance: original.DatabaseInstance)).IsSuccess.ShouldBeTrue();
        _requests.Count.ShouldBe(2);
    }

    private async Task<(ConnectionBindingService Bindings, IdentityBinding Actor, IdentityBinding Sibling)> ArrangeAsync()
    {
        Directory.CreateDirectory(Root);
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--quiet");
        using (var git = Process.Start(start) ?? throw new InvalidOperationException("Git did not start."))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await git.WaitForExitAsync(timeout.Token); }
            finally { if (!git.HasExited) { git.Kill(entireProcessTree: true); await git.WaitForExitAsync(); } }
            git.ExitCode.ShouldBe(0);
        }
        Directory.Exists(TwigDir).ShouldBeFalse();
        var refresher = Substitute.For<ITokenRefresher>();
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (Token(call.ArgAt<string>(0)), call.ArgAt<string>(0), false));
        var bindings = _bindings = new ConnectionBindingService(Path.Combine(_temp, "home"), refresher);
        foreach (var (name, oid) in new[] { ("actor", Actor), ("sibling", Sibling) })
            (await bindings.RegisterAadIdentityAsync(name, new()
            {
                RefreshToken = name, ClientId = "fixture-client", TenantId = Tenant,
                ObjectId = oid, AuthorityHost = "login.microsoftonline.com",
            })).ObjectId.ShouldBe(oid);
        var actor = await bindings.CreateBindingAsync("fixture", "Project", "actor", makeDefault: true);
        var sibling = await bindings.CreateBindingAsync("fixture", "Project", "sibling", makeDefault: false);
        actor.IdentityId.ShouldNotBe(sibling.IdentityId);
        return (bindings, actor, sibling);
    }

    private NativeWorkspaceSetupArguments Arguments(string? store = null)
        => new(Root, "fixture", "Project", null, null, "ado", null, null, null, store, null,
            Path.Combine(_temp, "display.json"));

    private CoreTransportOverride Transport(Func<Task>? beforeProject = null)
        => new(() => new MetadataHandler(async request =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            request.RequestUri!.Host.ShouldBe("dev.azure.com");
            request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
            request.Headers.Authorization.Parameter.ShouldBe(Token("actor"));
            var path = request.RequestUri.AbsolutePath;
            _requests.Add(path);
            if (path == "/fixture/_apis/connectionData")
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"instanceId":"55555555-5555-5555-5555-555555555555","authenticatedUser":{"id":"{{Principal}}"}}"""),
                };
            path.ShouldBe("/fixture/_apis/projects/Project");
            if (beforeProject is not null) await beforeProject();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"66666666-6666-6666-6666-666666666666"}"""),
            };
        }));

    private static string Token(string identity)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = JwtTestFactory.AdoResourceId, ["tid"] = Tenant,
            ["oid"] = identity == "actor" ? Actor : Sibling, ["iss"] = "https://sts.windows.net/" + Tenant + "/",
            ["exp"] = 4102444800L,
        });
        return "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_') + ".signature";
    }

    private sealed class MetadataHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    public void Dispose()
    {
        _bindings?.Dispose();
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_temp)) return;
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_temp, recursive: true);
    }
}
