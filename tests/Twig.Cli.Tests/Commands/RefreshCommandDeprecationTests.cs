using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class RefreshCommandDeprecationTests : RefreshCommandTestBase
{
    private const string ExpectedHint = "hint: 'twig refresh' is deprecated. Use 'twig sync' instead.";

    private TwigCommands CreateCommands()
    {
        var services = new ServiceCollection()
            .AddSingleton(CreateRefreshCommand())
            .BuildServiceProvider();
        return new TwigCommands(services);
    }

    [Fact]
    public async Task Refresh_WritesDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.Refresh(ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldContain(ExpectedHint);
    }

    [Fact]
    public async Task Refresh_DelegatesToRefreshCommand_AfterHint()
    {
        var commands = CreateCommands();

        await StderrCapture.RunAsync(
            () => commands.Refresh(output: "json", ct: CancellationToken.None));

        await _adoService.Received(1).QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
