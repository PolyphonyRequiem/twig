using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GhCliTokenSourceTests
{
    private const string Secret = "gho_SECRETtoken123";

    [Fact]
    public async Task Success_RunsGhAuthTokenWithArgumentArrayAndBounds_AndCachesInMemory()
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 0, Secret + "\n"));
        var source = new GhCliTokenSource(runner, "github.com");

        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);

        runner.Calls.Count.ShouldBe(1);
        var call = runner.Calls[0];
        call.FileName.ShouldBe("gh");
        call.Arguments.ShouldBe(["auth", "token", "--hostname", "github.com"]);
        call.Timeout.ShouldBe(GhCliTokenSource.Timeout);
        call.Timeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        call.MaxOutputChars.ShouldBe(GhCliTokenSource.MaxOutputChars);
    }

    public static TheoryData<string, int, string, string> Failures => new()
    {
        { nameof(BoundedProcessOutcome.NotStarted), -1, "", "not found" },
        { nameof(BoundedProcessOutcome.TimedOut), -1, "", "did not finish in time" },
        { nameof(BoundedProcessOutcome.OutputLimitExceeded), -1, "", "unexpected output" },
        { nameof(BoundedProcessOutcome.Exited), 1, Secret, "exited with code 1" },
        { nameof(BoundedProcessOutcome.Exited), 0, "", "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, "   \n", "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, Secret + "\n" + Secret, "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, "not logged in " + Secret, "no usable token" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failure_FailsClosedWithActionableMessage_AndNeverDisclosesOutput(
        string outcomeName, int exitCode, string stdout, string expected)
    {
        var outcome = Enum.Parse<BoundedProcessOutcome>(outcomeName);
        var runner = new FakeRunner(new(outcome, exitCode, stdout));
        var source = new GhCliTokenSource(runner, "ghe.example.com");

        var ex = await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            () => source.GetTokenAsync(CancellationToken.None).AsTask());

        ex.Message.ShouldContain(expected);
        ex.Message.ShouldContain("gh auth login --hostname ghe.example.com");
        ex.Message.ShouldNotContain(Secret);
        ex.ToString().ShouldNotContain(Secret);
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Failure_IsNotCached_SoALaterLoginIsPickedUp()
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 1, ""));
        var source = new GhCliTokenSource(runner, "github.com");
        await Should.ThrowAsync<GitHubCredentialUnavailableException>(() => source.GetTokenAsync(CancellationToken.None).AsTask());

        runner.Next = new(BoundedProcessOutcome.Exited, 0, Secret);
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);
        runner.Calls.Count.ShouldBe(2);
    }

    internal sealed class FakeRunner(BoundedProcessResult result) : IBoundedProcessRunner
    {
        public BoundedProcessResult Next { get; set; } = result;
        public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, int MaxOutputChars)> Calls { get; } = [];

        public Task<BoundedProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputChars, CancellationToken ct)
        {
            Calls.Add((fileName, arguments.ToArray(), timeout, maxOutputChars));
            return Task.FromResult(Next);
        }
    }
}
