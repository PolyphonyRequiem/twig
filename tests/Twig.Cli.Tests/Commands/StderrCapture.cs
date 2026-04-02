namespace Twig.Cli.Tests.Commands;

internal static class StderrCapture
{
    internal static async Task<(int result, string stderr)> RunAsync(Func<Task<int>> action)
    {
        var sw = new StringWriter();
        Console.SetError(sw);
        try { return (await action(), sw.ToString()); }
        finally { Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }); }
    }
}
