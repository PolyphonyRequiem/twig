/// <summary>
/// Per-command usage examples appended to <c>--help</c> output after ConsoleAppFramework's
/// built-in help text. Handles compound commands (e.g. <c>nav up</c>, <c>seed new</c>) and
/// single-token commands (e.g. <c>set</c>, <c>flow-start</c>).
/// </summary>
internal static class CommandExamples
{
    /// <summary>
    /// Command name → array of example lines. Each line is a complete example string
    /// (e.g. <c>"twig set 1234              Set context by work item ID"</c>).
    /// Compound commands use space-separated keys (e.g. <c>"nav up"</c>).
    /// </summary>
    internal static Dictionary<string, string[]> Examples { get; } = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// Resolves the command name from <paramref name="args"/> and prints usage examples
    /// if any are registered. When <c>args.Length &gt;= 2</c>, tries the compound key
    /// <c>"{args[0]} {args[1]}"</c> first (e.g. <c>"nav up"</c>), then falls back to
    /// <c>args[0]</c> (e.g. <c>"set"</c>). Does nothing if no examples match.
    /// </summary>
    internal static void ShowIfPresent(string[] args)
    {
        if (args.Length == 0) return;

        var compound = args.Length >= 2 ? $"{args[0]} {args[1]}" : null;
        var key = compound is not null && Examples.ContainsKey(compound) ? compound : args[0];

        if (!Examples.TryGetValue(key, out var examples))
            return;

        Console.WriteLine();
        Console.WriteLine("Examples:");
        foreach (var example in examples)
            Console.WriteLine($"  {example}");
    }
}
