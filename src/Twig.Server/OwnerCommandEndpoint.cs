using System.Buffers.Binary;
using System.Text.Json;
using Twig.Commands;
using Twig.Infrastructure.Services;
using Twig.Rendering;

namespace Twig.Server;

internal sealed class OwnerCommandEndpoint(Guid boot, string home, UserBenchStore benches,
    IServiceProvider? hostServices = null, CancellationToken stopping = default)
{
    private readonly OwnerUpdateCommand updates = new(boot, home, benches, hostServices, stopping);
    internal Task DrainAsync() => updates.DrainAsync();
    internal async Task<UpdateReply> CanvasUpdate(string[] args, NativeSourceStamp source, string? baseline,
        Func<bool> stillAuthorized, string? frozenText, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var result = await updates.ExecuteAsync(args, Path.Combine(Path.GetDirectoryName(home)!, "work"),
            () => Task.FromResult(frozenText ?? throw new InvalidDataException("Missing frozen input.")), _ => Task.CompletedTask,
            stdout, stderr, ct, new(source, baseline, stillAuthorized));
        if (result == 2) throw new HttpFailure(409, "ReviewRequired", "The reviewed item or authority is no longer available.");
        return JsonSerializer.Deserialize(stdout.ToString(), UpdateReplyJson.Default.UpdateReply)
            ?? throw new InvalidDataException("The original action reply was missing.");
    }
    internal static async Task RefuseLegacyAsync(Stream client, CancellationToken token)
    {
        // The pinned client finishes its invocation write before reading. Drain it in fixed
        // space, without deserializing arguments or resolving a handler.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        var header = new byte[5];
        await client.ReadExactlyAsync(header, budget.Token);
        var remaining = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (header[0] != (byte)FrameKind.Invocation || remaining is < 0 or > CommandProtocol.MaximumBytes) return;
        var scratch = new byte[4096];
        while (remaining > 0)
        {
            var count = Math.Min(remaining, scratch.Length);
            await client.ReadExactlyAsync(scratch.AsMemory(0, count), budget.Token);
            remaining -= count;
        }
        await Refuse(client, budget.Token);
    }

    private static async Task Refuse(Stream client, CancellationToken token)
    {
        await CommandProtocol.WriteAsync(client, FrameKind.Stderr,
            "Incompatible Twig CLI and server command protocol. No command was run. Update the CLI or server so their command protocols match." + Environment.NewLine, token);
        await CommandProtocol.WriteAsync(client, FrameKind.Exit, "2", token);
    }

    internal async Task ServeAsync(Stream client, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        if (await CommandProtocol.ReadAsync(client, budget.Token, 128) != (FrameKind.Negotiate, OwnerFixtureSelection.Protocol))
        { await Refuse(client, budget.Token); return; }
        await CommandProtocol.WriteAsync(client, FrameKind.Negotiate, $"{OwnerFixtureSelection.Protocol}/{boot:D}", budget.Token);
        var frame = await CommandProtocol.ReadAsync(client, budget.Token, 16384);
        var request = frame.Kind == FrameKind.Invocation ? JsonSerializer.Deserialize(frame.Text, CommandWireJson.Default.CommandInvocation) : null;
        if (request is not { Version: 2 } || !OwnerFixtureSelection.Same(request.MetadataHome, home))
        { await Refuse(client, budget.Token); return; }

        // Only these native-store commands are qualified here. Never fall back to a
        // legacy command worker or compose a caller-selected workspace in this process.
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var command = new UserBenchCommand(benches, new RendererFactory());
        var result = request.Arguments switch
        {
            ["plan", ..] => await OwnerPlanCommand.ExecuteAsync(home, benches, request.Directory, request.Arguments, output, errors, token),
            ["update" or "note" or "seed", ..] => await updates.ExecuteAsync(request.Arguments, request.Directory, async () =>
            {
                await CommandProtocol.WriteAsync(client, FrameKind.ReadBody, "", token);
                var input = await CommandProtocol.ReadAsync(client, token);
                return input.Kind == FrameKind.Text ? input.Text : throw new InvalidDataException("Missing frozen update input.");
            }, async identity =>
            {
                try { await CommandProtocol.WriteAsync(client, FrameKind.Bound, identity, token); }
                catch (IOException) { /* The client detached; the original remains owned by this daemon. */ }
            }, output, errors, token),
            ["bench", "list", "-o", var format] => await command.ExecuteAsync("list", null, format, token, stdout: output, stderr: errors),
            ["bench", "create", var name, "-o", var format] => await command.ExecuteAsync("create", name, format, token, stdout: output, stderr: errors),
            _ => 2
        };
        if (result == 2 && errors.GetStringBuilder().Length == 0) errors.WriteLine("Command not admitted by the isolated execution owner.");
        await CommandProtocol.WriteAsync(client, FrameKind.Stdout, output.ToString(), token);
        await CommandProtocol.WriteAsync(client, FrameKind.Stderr, errors.ToString(), token);
        await CommandProtocol.WriteAsync(client, FrameKind.Exit, result.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
    }
}
