using System.Reflection;
using Shouldly;
using Twig.Commands;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class GovernanceFreeConnectionContractTests
{
    [Theory]
    [InlineData(typeof(TwigCommands), nameof(TwigCommands.ConnectionReconcileWrite))]
    [InlineData(typeof(ConnectionWritesCommand), nameof(ConnectionWritesCommand.ExecuteAsync))]
    [InlineData(typeof(IConnectionRemoteWriteReconciliationService), nameof(IConnectionRemoteWriteReconciliationService.ReconcileAsync))]
    public void ConnectionReconciliationDoesNotAcceptACallerAuthorizer(Type contract, string methodName)
    {
        var method = contract.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull().GetParameters().ShouldNotContain(parameter =>
            parameter.Name == "authorize" || parameter.Name == "authorizer",
            "--authorize is removed, not retained as an annotation shim");
    }

    [Fact]
    public void ReconciliationRetainsExactIntentAndEvidenceParameters()
    {
        var method = typeof(IConnectionRemoteWriteReconciliationService)
            .GetMethod(nameof(IConnectionRemoteWriteReconciliationService.ReconcileAsync));
        var parameters = method.ShouldNotBeNull().GetParameters();

        foreach (var name in new[] { "intentId", "confirmedDigest", "rationale" })
            parameters.ShouldContain(parameter => parameter.Name == name && parameter.ParameterType == typeof(string));
    }
}
