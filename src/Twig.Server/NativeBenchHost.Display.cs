namespace Twig.Server;

public static partial class NativeBenchHost
{
    private static void MapReleasedDisplay(WebApplication app, ViewSessions sessions, ServerOptions options)
    {
        var display = app.Services.GetRequiredService<ReleasedBenchDisplay>();
        app.MapGet("/api/v2/views/{view:guid}/benches/{bench:guid}/partitions",
            (HttpContext context, Guid view, Guid bench) =>
                display.ReadPartitions(sessions, view, BenchHost.Bearer(context), bench, directory =>
                    context.Response.WriteAsJsonAsync(directory, DisplayJson.Default.ReleasedPartitions,
                        contentType: "application/json", cancellationToken: context.RequestAborted), context.RequestAborted));
        app.MapGet("/api/v2/views/{view:guid}/benches/{bench:guid}/partitions/{partition:guid}/page",
            async (HttpContext context, Guid view, Guid bench, Guid partition) =>
            {
                var headers = context.Request.Headers;
                if (headers["X-Twig-Cursor"].Count > 1 || headers["X-Twig-Generation"].Count > 1)
                    throw new HttpFailure(400, "InvalidInput", "Supply at most one cursor and one generation.");
                await display.Read(sessions, view, BenchHost.Bearer(context), bench, partition,
                    headers["X-Twig-Cursor"].FirstOrDefault(), headers["X-Twig-Generation"].FirstOrDefault(), async frame =>
                    {
                        var output = context.Response.Headers;
                        output["X-Twig-Authority"] = options.ServerId.ToString("D");
                        output["X-Twig-Process"] = options.ProcessInstance.ToString("D");
                        output["X-Twig-Connection"] = ReleasedBenchDisplay.Fingerprint(frame.Scope.Connection, null);
                        output["X-Twig-Generation"] = frame.Generation.ToString("D");
                        output["X-Twig-Partition"] = partition.ToString("D");
                        output["X-Twig-Owner"] = frame.Scope.Owner.ToString("D");
                        output["X-Twig-Store"] = frame.Scope.Store.ToString("D");
                        if (frame.Scope.Origin is { } origin) output["X-Twig-Origin"] = origin.StoreInstance.ToString("D");
                        output["X-Twig-Auth-Epoch"] = frame.AuthEpoch.ToString("D");
                        output["X-Twig-Expires"] = frame.Expires.ToString("O");
                        output["X-Twig-Remaining-Ms"] = frame.RemainingMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        output["X-Twig-Watermark"] = frame.Watermark.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (frame.Next is not null) output["X-Twig-Next-Cursor"] = frame.Next;
                        context.Response.ContentType = "application/json";
                        await context.Response.Body.WriteAsync(frame.Body.AsMemory(), context.RequestAborted);
                    }, context.RequestAborted);
            });
    }
}
