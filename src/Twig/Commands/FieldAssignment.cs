using Twig.Core;

namespace Twig.Commands;

internal static class FieldAssignment
{
    internal static Result<IReadOnlyList<(string FieldName, string? NewValue)>> ParseAll(
        IReadOnlyList<string>? pairs, string flagName)
    {
        var parsed = new List<(string FieldName, string? NewValue)>();
        foreach (var pair in pairs ?? [])
        {
            var equals = pair.IndexOf('=');
            if (equals < 1)
                return Result<IReadOnlyList<(string FieldName, string? NewValue)>>.Fail(ErrorCode.InvalidInput,
                    $"Invalid {flagName} format: '{pair}'. Expected fieldReferenceName=value.");
            parsed.Add((pair[..equals], pair[(equals + 1)..]));
        }
        return Result<IReadOnlyList<(string FieldName, string? NewValue)>>.Ok(parsed);
    }
}
