using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig;

internal static class TwigConsoleApp
{
    internal static Action<string[]> CreateRunner(Action<IServiceCollection> configureServices)
    {
        string? compositionError = null;
        var app = ConsoleApp.Create().ConfigureServices(services =>
        {
            try { configureServices(services); }
            catch (InvalidOperationException ex) { compositionError = ex.Message; throw; }
        });
        app.UseFilter<global::ExceptionFilter>();
        app.Add<TwigCommands>();
        app.Add<SkillCommands>("skills");
        return args =>
        {
            compositionError = null;
            var previous = ConsoleApp.LogError;
            var format = StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default;
            if (CoreMutationAdmissionGuard.ValidateCatalog(args) is { } admission)
            {
                CommandError.Write(new RendererFactory(), Console.Error, format, admission);
                Environment.ExitCode = 1;
                return;
            }
            if (StructuredMutationArgumentValidator.Validate(args) is { } localTargetError)
            {
                CommandError.Write(new RendererFactory(), Console.Error, format, localTargetError);
                Environment.ExitCode = StructuredMutationArgumentValidator.UsageExitCode;
                return;
            }
            if (format != OutputFormats.Default)
                ConsoleApp.LogError = message => CommandError.Write(new RendererFactory(), Console.Error, format,
                    compositionError ?? message);
            try { app.Run(args); }
            finally { ConsoleApp.LogError = previous; }
        };
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
