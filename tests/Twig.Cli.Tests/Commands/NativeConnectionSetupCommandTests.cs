using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class NativeConnectionSetupCommandTests
{
    [Fact]
    public void PublicHomeResolverDelegatesWithoutCreatingNativeState()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-home-resolution-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var previous = Environment.GetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar);
        try
        {
            Directory.Exists(root).ShouldBeFalse();
            Environment.SetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar, home);

            var resolved = NativeConnectionSetup.ResolveUserHome();

            resolved.ShouldBe(ConnectionBindingService.ResolveUserHome());
            new NativeConnectionSetup(resolved).ShouldNotBeNull();
            Directory.Exists(root).ShouldBeFalse();
            Directory.Exists(Path.Combine(home, "credentials")).ShouldBeFalse();
            File.Exists(Path.Combine(home, "system.db")).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar, previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("relative-home")]
    public void PublicHomeResolverPreservesNativeUnsetOrInvalidBehavior(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar, value);
            if (value is null)
                NativeConnectionSetup.ResolveUserHome().ShouldBe(ConnectionBindingService.ResolveUserHome());
            else
            {
                var expected = Should.Throw<InvalidOperationException>(() => ConnectionBindingService.ResolveUserHome());
                var actual = Should.Throw<InvalidOperationException>(() => NativeConnectionSetup.ResolveUserHome());
                actual.Message.ShouldBe(expected.Message);
            }
        }
        finally { Environment.SetEnvironmentVariable(ConnectionBindingService.UserHomeEnvVar, previous); }
    }

    [Fact]
    public void MissingEnrollmentAliasRefusesBeforeNativeHomeCreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-enrollment-admission-" + Guid.NewGuid().ToString("N"));
        var start = Path.Combine(root, "checkout");
        var home = Path.Combine(root, "native-home");
        var credentials = Path.Combine(home, "credentials");
        var registry = Path.Combine(home, "system.db");
        Directory.CreateDirectory(start);
        var previousHome = Environment.GetEnvironmentVariable("TWIG_USER_HOME");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Directory.Exists(home).ShouldBeFalse();
            Directory.Exists(credentials).ShouldBeFalse();
            File.Exists(registry).ShouldBeFalse();
            Directory.Exists(Path.Combine(start, ".twig")).ShouldBeFalse();
            File.Exists(Path.Combine(start, "twig.json")).ShouldBeFalse();
            Environment.SetEnvironmentVariable("TWIG_USER_HOME", home);
            Console.SetOut(output);
            Console.SetError(error);
            string[] args = ["auth", "login"];

            var exit = TwigConsoleApp.Run(args, services =>
                services.AddTwigWorkspaceServices(start, args, Path.Combine(root, "display.json")));

            exit.ShouldBe(2, error.ToString());
            error.ToString().ShouldContain("Named enrollment requires --identity");
            output.ToString().ShouldBeEmpty();
            File.Exists(registry).ShouldBeFalse("the lazy registry must not be confused with constructor directory effects");
            Directory.Exists(home).ShouldBeFalse("missing alias must refuse before native binding service construction");
            Directory.Exists(credentials).ShouldBeFalse();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("TWIG_USER_HOME", previousHome);
            Directory.Delete(root, recursive: true);
        }
    }
}
