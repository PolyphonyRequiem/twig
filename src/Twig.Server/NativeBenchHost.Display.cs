namespace Twig.Server;

[System.Text.Json.Serialization.JsonSerializable(typeof(ReleasedBenchChoice[]))]
internal sealed partial class TransportJson;
internal sealed record ReleasedBenchChoice(Guid Id, string Name);

public static partial class NativeBenchHost
{
    private static void MapReleasedDisplay(WebApplication app, ViewSessions sessions, ServerOptions options, Bench.Infrastructure.NativePanel panel)
    {
        var display = app.Services.GetRequiredService<ReleasedBenchDisplay>();
        app.MapGet("/api/v2/views/{view:guid}/benches/{bench:guid}/partitions",
            (HttpContext context, Guid view, Guid bench) =>
                display.ReadPartitions(sessions, view, BenchHost.Bearer(context), bench, directory =>
                    context.Response.WriteAsJsonAsync(directory, DisplayJson.Default.ReleasedPartitions,
                        contentType: "application/json", cancellationToken: context.RequestAborted), context.RequestAborted));
        MapReleasedInputs(app, sessions, options, display, panel.Definitions);
        app.MapGet("/api/v2/views/{view:guid}/benches", async (HttpContext context, Guid view) =>
        {
            var bearer = BenchHost.Bearer(context);
            var ownerStore = sessions.WithDisplayOwner(view, bearer, s => s.CatalogId);
            var inventory = BenchHost.Require(await panel.ListAsync(context.RequestAborted));
            if (inventory.InstanceId != ownerStore) throw new HttpFailure(403, "PolicyViolation", "The owner store changed.");
            var choices = inventory.Benches.Select(bench => new ReleasedBenchChoice(bench.Id, bench.Name)).ToArray();
            _ = sessions.WithDisplayOwner(view, bearer, s => s.CatalogId);
            await BenchHost.Write(context, choices);
        });
        app.MapGet("/api/v2/views/{view:guid}/authorization", (HttpContext context, Guid view) =>
            display.StreamAuthorization(sessions, view, BenchHost.Bearer(context), context, options));
        app.MapGet("/api/v2/views/{view:guid}/benches/{bench:guid}/partitions/{partition:guid}/page",
            async (HttpContext context, Guid view, Guid bench, Guid partition) =>
            {
                var headers = context.Request.Headers;
                if (headers["X-Twig-Cursor"].Count > 1 || headers["X-Twig-Generation"].Count > 1)
                    throw new HttpFailure(400, "InvalidInput", "Supply at most one cursor and one generation.");
                await display.Read(sessions, view, BenchHost.Bearer(context), bench, partition,
                    headers["X-Twig-Cursor"].FirstOrDefault(), headers["X-Twig-Generation"].FirstOrDefault(),
                    frame => WriteReleasedFrame(context, options, frame), context.RequestAborted);
            });
    }
}
