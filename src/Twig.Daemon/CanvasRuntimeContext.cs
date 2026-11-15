using System.Text.Json.Serialization;

namespace Bench.Server;

public sealed record CanvasRuntimeContext(string SessionId, string? WorkingDirectory = null,
    string? GitRoot = null, string? Repository = null, string? Branch = null, bool? IsRemote = null);
public sealed record RegisterCanvasBody(CanvasRuntimeContext? Context = null);

[JsonSerializable(typeof(CanvasRuntimeContext))]
[JsonSerializable(typeof(RegisterCanvasBody))]
internal sealed partial class TransportJson;
