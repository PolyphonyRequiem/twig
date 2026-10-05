using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

public sealed class ConnectionCheckConsumerTests
{
    [Fact]
    public async Task ActualMcpPreviewKeepsBothFieldAndNoteBlockersAndCannotPublishOrAdoptMissingSelection()
    {
        var temp = Path.Combine(Path.GetTempPath(), "twig-mcp-eligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = temp, ArgumentList = { "init", "--quiet" }, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            await git.WaitForExitAsync();
            git.ExitCode.ShouldBe(0);
            var config = new TwigConfiguration { Organization = "fixture", Project = "Work" };
            config.Display.Hints = false;
            var paths = TwigPaths.BuildPaths(Path.Combine(temp, ".twig"), config, temp);
            await config.SaveSplitAsync(paths);
            using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
                (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
            var home = Path.Combine(temp, "home");
            using var transport = new IdentityTransport();
            using var http = new HttpClient(transport);
            using var bindings = new ConnectionBindingService(home, patHttpClient: http);
            using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(home, "system.db"), TimeProvider.System))
            {
                var anchor = WorktreeAnchorDetector.Detect(temp)!.Value;
                var connection = ConnectionRefResolver.Compute(config);
                (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
                (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
            }
            await bindings.RegisterPatIdentityAsync("actor", config.Organization, "fixture-pat");
            await bindings.CreateBindingAsync(config.Organization, config.Project, "actor", makeDefault: true);
            var services = new ServiceCollection();
            services.AddSingleton<IConnectionBindingService>(bindings);
            services.AddConnectionServices(config, paths.TwigDir, temp);
            services.AddSingleton(paths);
            services.AddSingleton<ISystemWorktreeRegistry>(_ => new SqliteSystemWorktreeRegistry(Path.Combine(home, "system.db"), TimeProvider.System));
            services.AddSingleton(http);
            services.AddTwigNetworkServices(config);
            using var provider = services.BuildServiceProvider();
            var pending = provider.GetRequiredService<IPendingChangeStore>();
            await pending.AddChangeAsync(42, "field", "System.Title", "Original", "Edited");
            await pending.AddChangeAsync(42, "note", null, null, "Unpublished note");
            var connectionKey = new Connection(config.Organization, config.Project);
            using var scope = new ConnectionScope(connectionKey, provider);
            var resolver = new ConnectionResolver(new ConnectionRegistry(paths.TwigDir, temp), new ScopeFactory(scope));
            var tools = new AdminTools(resolver);
            var before = (await provider.GetRequiredService<IPrimaryScopeAttachmentStore>().ReadWithRevisionAsync()).Value.Revision;
            var result = await tools.ConnectionCheck();
            using (var output = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text))
            {
                var body = output.RootElement.GetProperty("data");
                body.GetProperty("eligible").GetBoolean().ShouldBeFalse();
                body.GetProperty("pendingEdits").EnumerateArray().Select(row => row.GetProperty("kind").GetString())
                    .ShouldBe(new[] { "field", "note" });
                body.GetProperty("currentBindingId").GetString().ShouldNotBeNullOrWhiteSpace();
            }
            (await pending.GetChangesAsync(42)).Count.ShouldBe(2);
            transport.WorkRequests.ShouldBe(0);
            (await provider.GetRequiredService<IPrimaryScopeAttachmentStore>().ReadWithRevisionAsync()).Value.Revision.ShouldBe(before);
            await pending.ClearChangesAsync(42); // Explicit fixture discard, never performed by the preview.
            result = await tools.ConnectionCheck();
            using (var output = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text))
                output.RootElement.GetProperty("data").GetProperty("eligible").GetBoolean().ShouldBeTrue();
            transport.WorkRequests.ShouldBe(0);
            using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(home, "system.db")}"))
            {
                database.Open();
                using var remove = database.CreateCommand();
                remove.CommandText = "DELETE FROM connection_defaults WHERE connection_ref = @connection";
                remove.Parameters.AddWithValue("@connection", ConnectionRefResolver.Compute(config));
                remove.ExecuteNonQuery().ShouldBe(1);
            }
            result = await tools.ConnectionCheck();
            using (var output = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text))
            {
                var body = output.RootElement.GetProperty("data");
                body.GetProperty("eligible").GetBoolean().ShouldBeFalse();
                body.GetProperty("currentBindingId").ValueKind.ShouldBe(JsonValueKind.Null);
                body.GetProperty("unknownRows").EnumerateArray().ShouldContain(row => row.GetProperty("source").GetString() == "binding-selection");
            }
            transport.WorkRequests.ShouldBe(0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
    }

    private sealed class ScopeFactory(ConnectionScope scope) : IConnectionScopeFactory
    {
        public ConnectionScope GetOrCreate(Connection connection) => scope.Connection == connection
            ? scope : throw new KeyNotFoundException("Unknown fixture attachment.");
    }

    private sealed class IdentityTransport : HttpMessageHandler
    {
        public int WorkRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"authenticatedUser\":{\"id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"providerDisplayName\":\"Fixture actor\"}}", Encoding.UTF8, "application/json")
                });
            WorkRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"message\":\"Normal work HTTP must not occur during eligibility inspection\"}", Encoding.UTF8, "application/json") });
        }
    }
}
