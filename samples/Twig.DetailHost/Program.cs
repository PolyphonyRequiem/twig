using System.Text.Json;
using Twig.RenderTree;

namespace Twig.DetailHost;

/// <summary>An external net10 consumer of the public, I/O-free RenderTree boundary.</summary>
internal static class Program
{
    private static int Main()
    {
        const string title = "Caller-owned detail [with markup] and full text";
        var tree = new RenderTree.RenderTree([new RenderNode.Record("detail",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(42),
                ["title"] = RenderCell.String(title),
                ["empty"] = new("(empty)", new RenderValue.Null()),
                ["missing"] = RenderCell.DisplayOnly("(not supplied)"),
                ["enabled"] = RenderCell.Boolean(true),
            })]);
        using var json = new StringWriter();
        new JsonRenderer(json).Render(tree);
        using var parsed = JsonDocument.Parse(json.ToString());
        var root = parsed.RootElement;
        if (root.GetProperty("id").GetInt32() != 42 || root.GetProperty("title").GetString() != title
            || root.GetProperty("empty").ValueKind != JsonValueKind.Null
            || root.TryGetProperty("missing", out _) || !root.GetProperty("enabled").GetBoolean())
            return Fail("typed values, empty, absent and full source text must remain distinct");
        using var minimal = new StringWriter();
        new MinimalRenderer(minimal).Render(tree);
        if (!minimal.ToString().Contains(title, StringComparison.Ordinal))
            return Fail("the public plain renderer must retain complete caller text");
        using var ids = new StringWriter();
        new IdsRenderer(ids).Render(tree);
        if (ids.ToString() != "42" + Environment.NewLine)
            return Fail("the ID renderer must use the exact typed ID convention");
        Console.WriteLine(json);
        Console.WriteLine("PROBE OK: external net10 RenderTree consumer preserved typed, empty, absent and full values across JSON, minimal and IDs without native services.");
        return 0;
    }

    private static int Fail(string reason)
    {
        Console.Error.WriteLine("PROBE FAILED: " + reason);
        return 1;
    }
}
