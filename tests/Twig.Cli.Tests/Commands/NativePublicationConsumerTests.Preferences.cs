using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task RetiredSelectionsNeverAccumulateHistoryOrReadWork()
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        var before = Directory.GetFiles(paths.TwigDir, "*.json");
        var requests = _transport.TotalRequests;
        foreach (var args in new string[][] { ["set", "42"], ["set", "43"], ["nav", "up"], ["nav", "back"],
            ["nav", "fore"], ["nav", "history"], ["bench", "switch", "Fixture"] })
            WorkspaceInvoke(paths, args).Exit.ShouldNotBe(0, string.Join(" ", args));
        Directory.GetFiles(paths.TwigDir, "*.json").Order().ShouldBe(before.Order());
        _transport.TotalRequests.ShouldBe(requests);
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task UnreadableActivePreferenceDoesNotBlockExplicitBenchOrItemTargets()
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        var file = Path.Combine(paths.TwigDir, "core-context.json");
        await File.WriteAllTextAsync(file, "{invalid");
        var show = WorkspaceInvoke(paths, ["show", "42", "--refresh", "-o", "json"]);
        show.Exit.ShouldBe(0, show.Error);
        var create = WorkspaceInvoke(paths, ["bench", "create", "Fixture", "-o", "json"]);
        create.Exit.ShouldBe(0, create.Error);
        var named = WorkspaceInvoke(paths, ["workspace", "--bench", "Fixture", "-o", "json"]);
        named.Exit.ShouldBe(0, named.Error);
        (await File.ReadAllTextAsync(file)).ShouldBe("{invalid");
        var explicitHuman = WorkspaceInvoke(paths, ["show", "42", "-o", "human"]);
        explicitHuman.Exit.ShouldBe(0, explicitHuman.Error);
        var requests = _transport.TotalRequests;
        foreach (var format in new[] { "human", "json" })
        {
            var omitted = WorkspaceInvoke(paths, ["show", "-o", format]);
            omitted.Exit.ShouldBe(2, omitted.Error);
        }
        _transport.TotalRequests.ShouldBe(requests);
        (await File.ReadAllTextAsync(file)).ShouldBe("{invalid");
        _transport.PatchAttempts.ShouldBe(0);
    }
}
