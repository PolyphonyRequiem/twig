using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;

namespace Twig;

internal static class TwigConsoleApp
{
    internal static Action<string[]> CreateRunner(Action<IServiceCollection> configureServices)
    {
        var app = ConsoleApp.Create().ConfigureServices(configureServices);
        app.UseFilter<global::ExceptionFilter>();
        app.Add<TwigCommands>();
        app.Add<OhMyPoshCommands>("ohmyposh");
        app.Add<SkillCommands>("skills");
        return args => app.Run(args);
    }

    internal static int Run(string[] args, Action<IServiceCollection> configureServices)
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            CreateRunner(configureServices)(args);
            return Environment.ExitCode;
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }
}
