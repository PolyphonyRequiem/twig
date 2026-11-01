using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task GitHubNativeHostStatusAndEvidenceNeverFallbackToAdoOrAcquireCredentials()
    {
        using var f = new Fixture();
        await f.Initialize(explicitStore: false);
        File.Exists(Path.Combine(f.Root, ".twig", CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        var requests = f.Http.Requests;
        var credentials = f.CredentialCalls;
        var captured = await f.Snapshot();
        foreach (var route in new[] { new[] { "connection", "status" }, new[] { "auth", "status" } })
        {
            f.Run([.. route, "-o", "json"]).ShouldBe(0, f.Error);
            using var json = JsonDocument.Parse(f.Output);
            json.RootElement.GetProperty("provider").GetString().ShouldBe("github");
            json.RootElement.GetProperty("recordedActor").GetString().ShouldNotBeNullOrWhiteSpace();
            json.RootElement.GetProperty("freshlyAuthenticated").GetBoolean().ShouldBeFalse();
        }
        f.Run("connection", "writes", "-o", "json").ShouldBe(0, f.Error);
        f.Output.ShouldContain("No GitHub transport intents");
        f.Http.Requests.ShouldBe(requests);
        f.CredentialCalls.ShouldBe(credentials);
        (await f.Snapshot()).Version.ShouldBe(captured.Version);
    }

    [Fact]
    public async Task GitHubTransportInspectionCorrelatesOriginalAttemptWithoutSettlingUnknownCreate()
    {
        using var f = new Fixture();
        await f.Initialize(explicitStore: false);
        f.Run("seed", "new", "--no-parent", "--title", "Transport evidence", "--type", Twig.Providers.GitHub.GitHubProvider.Untyped, "-o", "json")
            .ShouldBe(0, f.Error);
        var draft = (await f.Snapshot()).Drafts.Single();
        f.Http.LoseAcknowledgement = true;
        f.Run("seed", "publish", "--all").ShouldBe(1, f.Error);
        var captured = await f.Snapshot();
        captured.Journal.Single().Attempt.ShouldNotBe(Guid.Empty);
        captured.Journal.Single().Status.ShouldBe(Twig.Core.OpStatus.Indeterminate);
        captured.Mappings.ShouldBeEmpty();
        var directory = Path.Combine(f.Root, ".twig", "core-github-intents");
        var intent = Directory.GetFiles(directory, "*.json").Single();
        var before = await File.ReadAllBytesAsync(intent);
        var requests = f.Http.Requests;
        var credentials = f.CredentialCalls;
        f.Run("connection", "writes", "-o", "json").ShouldBe(1, f.Error);
        f.Output.ShouldContain("unknown-transport-outcome");
        f.Output.ShouldContain(captured.Journal.Single().Attempt.ToString("D"));
        f.Output.ShouldContain(captured.Journal.Single().Digest);
        f.Output.ShouldContain("execution authority");
        f.Http.Requests.ShouldBe(requests);
        f.CredentialCalls.ShouldBe(credentials);
        (await f.Snapshot()).Version.ShouldBe(captured.Version);
        (await File.ReadAllBytesAsync(intent)).ShouldBe(before);
        (await f.Snapshot()).Drafts.Single().Key.ShouldBe(draft.Key);

        await File.WriteAllTextAsync(intent + ".ack", "{\"requestHash\":\"wrong\",\"statusCode\":200,\"body\":\"matching title is not proof\"}");
        f.Run("connection", "writes", "-o", "json").ShouldBe(1);
        f.Output.ShouldBeEmpty();
        using (var error = JsonDocument.Parse(f.Error))
            error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("hash/status");
        (await f.Snapshot()).Version.ShouldBe(captured.Version);
        (await File.ReadAllBytesAsync(intent)).ShouldBe(before);
        f.Run("seed", "publish", "--all").ShouldBe(1);
        f.Http.Writes.ShouldBe(1, "transport inspection cannot remove the original unknown-create fence");
    }
}
