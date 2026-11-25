using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Twig.Cli.Tests.Commands;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Shouldly;

// Loaded only by the child test daemon. The matched host, IPC, real stores,
// credentials/bindings and provider adapters stay intact; replace external HTTP.
internal static class StartupHook
{
    private sealed class Observer<T>(Action<T> accept) : IObserver<T>
    {
        public void OnNext(T value) => accept(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    public static void Initialize()
    {
        var provider = Environment.GetEnvironmentVariable("TWIG_TEST_REMOTE_PROVIDER");
        if (provider is not ("ado" or "github" or "cas")) throw new InvalidOperationException("Expected test provider.");
        DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name != "Microsoft.Extensions.Hosting") return;
            listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
            {
                if (entry is { Key: "HostBuilding", Value: IHostBuilder builder })
                    builder.ConfigureServices((_, services) =>
                    {
                        if (provider == "cas")
                        {
                            services.AddSingleton<Func<IStore,IStore>>(_ => store => new RaceReceipt(store));
                            return;
                        }
                        var makeHandler = provider == "ado" ? NativePublicationConsumerTests.OwnerFakeHttp()
                            : GitHubCoreSpineTests.OwnerFakeHttp();
                        services.AddSingleton(new CoreTransportOverride(() => new ParkResponse(makeHandler(), provider)));
                        services.AddSingleton<IGitHubTokenSource>(new FixtureToken());
                    });
            }));
        }));
    }

    private sealed class RaceReceipt(IStore inner) : IStore
    {
        public Task<Result<StoreSnapshot>> ReadAsync(Connection c, CancellationToken ct) => inner.ReadAsync(c, ct);
        public async Task<Result<long>> CommitAsync(Connection c, long version, StoreSnapshot next, CancellationToken ct)
        {
            if (next.Journal.Any(row => row.Status == OpStatus.Applied))
            {
                var current = (await inner.ReadAsync(c, ct)).Value;
                var seed = new Seed(ItemKey.Local(Guid.NewGuid()), Twig.Providers.Native.NativeProvider.ItemType, FieldValues.Empty);
                (await inner.CommitAsync(c, current.Version, new(current.Version + 1, current.Items, current.Relationships,
                    current.RetainedItems.Add(seed), current.Plans, current.Journal, current.Mappings, current.Benches), ct)).IsSuccess.ShouldBeTrue();
            }
            return await inner.CommitAsync(c, version, next, ct);
        }
    }

    private sealed class FixtureToken : IGitHubTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult("synthetic-secret");
    }

    private sealed class ParkResponse(HttpMessageHandler inner, string provider) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var mutation = provider == "ado" ? request.Method == HttpMethod.Patch :
                request.Content is not null && (await request.Content.ReadAsStringAsync(ct)).Contains("\"query\":\"mutation", StringComparison.Ordinal);
            var response = await base.SendAsync(request, ct);
            if (mutation)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var itemA = provider == "ado" ? request.RequestUri!.AbsolutePath.EndsWith("/42", StringComparison.Ordinal)
                    : body.Contains("\"I_opaque\"", StringComparison.Ordinal);
                var root = Path.GetDirectoryName(Environment.GetEnvironmentVariable("TWIG_USER_HOME")!)!;
                await File.WriteAllTextAsync(Path.Combine(root, itemA ? "provider-a" : "provider-b"), "received", ct);
                if (itemA) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            return response;
        }
    }
}

namespace Twig.Cli.Tests.Commands
{
    public sealed partial class GitHubCoreSpineTests
    {
        internal static Func<HttpMessageHandler> OwnerFakeHttp()
        {
            var http = new NativeHttp();
            http.OtherIssue = Other(43); http.ThirdIssue = Other(44);
            return http.NewHandle;
            JsonObject Other(int number)
            {
                var item = (JsonObject)http.Issue.DeepClone();
                item["number"] = number; item["id"] = "I_" + number;
                return item;
            }
        }
    }

    public sealed partial class NativePublicationConsumerTests
    {
        internal static Func<HttpMessageHandler> OwnerFakeHttp()
        {
            var http = new PrincipalTransport { CoreMetadataEnabled = true };
            foreach (var id in new[] { 42, 43, 44 }) http.SetItem(id, "original", 1, Actor);
            return http.NewHandle;
        }

        internal async Task InitializeOwnerWorkspace(string home, string work)
        {
            _ownerHome = home;
            using var client = new HttpClient(_transport);
            using var bindings = new ConnectionBindingService(home, patHttpClient: client);
            _bindings = bindings;
            using var registry = new Twig.Infrastructure.Persistence.SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
            _systemRegistry = registry;
            _http = client;
            try
            {
                var runtime = await AttachAsync(canonicalRoot: true, selectedRoot: work);
                var paths = runtime.GetRequiredService<TwigPaths>();
                var config = runtime.GetRequiredService<TwigConfiguration>();
                var origin = await bindings.ResolveAsync(config, paths);
                var connection = new Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
                    "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
                var path = Path.Combine(paths.TwigDir, "core.db");
                var store = (await SqliteStore.CreateAsync(path)).Value;
                (await CoreWorkspaceFactory.RecordNewAsync(paths, store, path, connection, Actor, default)).IsSuccess.ShouldBeTrue();
                await ActivateNativeSafetyFixtureAsync(paths, store, connection);
            }
            finally
            {
                foreach (var runtime in _runtimes) runtime.Dispose();
                _transport.Dispose();
                Directory.Delete(_temp, recursive: true);
            }
        }
    }
}
