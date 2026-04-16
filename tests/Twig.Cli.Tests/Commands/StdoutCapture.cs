namespace Twig.Cli.Tests.Commands;

internal static class StdoutCapture
{
    internal static async Task<(int result, string stdout)> RunAsync(Func<Task<int>> action)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { return (await action(), sw.ToString()); }
        finally { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); }
    }
}
