using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SaveCommandDeprecationTests
{
    private const string ExpectedHint = "hint: 'twig save' is deprecated. Use 'twig sync' instead.";

    private static (TwigCommands commands, IPendingChangeFlusher flusher) CreateCommandsWithMockedSave()
    {
        var flusher = Substitute.For<IPendingChangeFlusher>();
        flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        var saveCommand = new SaveCommand(
            Substitute.For<IWorkItemRepository>(),
            Substitute.For<IPendingChangeStore>(),
            flusher,
            new ActiveItemResolver(
                Substitute.For<IContextStore>(),
                Substitute.For<IWorkItemRepository>(),
                Substitute.For<IAdoWorkItemService>()),
            formatterFactory);

        var services = new ServiceCollection()
            .AddSingleton(saveCommand)
            .BuildServiceProvider();

        return (new TwigCommands(services), flusher);
    }

    [Fact]
    public async Task Save_WritesDeprecationHint_ToStderr()
    {
        var (commands, _) = CreateCommandsWithMockedSave();

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.Save(all: true, ct: CancellationToken.None));

        stderr.ShouldContain(ExpectedHint);
    }

    [Fact]
    public async Task Save_DelegatesToSaveCommand_AfterHint()
    {
        var (commands, flusher) = CreateCommandsWithMockedSave();

        await StderrCapture.RunAsync(
            () => commands.Save(all: true, output: "json", ct: CancellationToken.None));

        await flusher.Received(1).FlushAllAsync("json", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_PropagatesNonZeroExitCode()
    {
        var (commands, flusher) = CreateCommandsWithMockedSave();
        flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 0, 0, [new FlushItemFailure(99, "conflict")]));

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.Save(all: true, ct: CancellationToken.None));

        exitCode.ShouldBe(1);
        stderr.ShouldContain(ExpectedHint);
    }
}
