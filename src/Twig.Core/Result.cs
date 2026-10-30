using System.Collections.Immutable;

namespace Twig.Core;

public enum ErrorCode
{
    InvalidInput, NotFound, Unsupported, Unavailable, Conflict, SchemaChanged,
    DigestMismatch, PolicyViolation, Partial, Indeterminate, StorageUnavailable,
}

public sealed record Error(ErrorCode Code, string Message)
{
    public ImmutableArray<string> Evidence { get; init; } = [];
}

public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value) => _value = value;
    private Result(Error error) => _error = error;

    public bool IsSuccess => _error is null;
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException(_error!.Message);
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");
    public static Result<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)));
    public static Result<T> Fail(ErrorCode code, string message) => new(new Error(code, message));
    public static Result<T> Fail(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

internal static class Contract
{
    internal static string Text(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value != value.Trim())
            throw new ArgumentException("Canonical identifiers cannot have surrounding whitespace.", name);
        return value;
    }

    internal static ImmutableArray<T> Copy<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToImmutableArray();
        if (result.Any(v => v is null))
            throw new ArgumentException("A collection cannot contain null.", nameof(values));
        return result;
    }

    internal static ImmutableArray<T> Unique<T>(IEnumerable<T> values, Func<T, string> key)
    {
        var result = Copy(values);
        if (result.Select(key).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Duplicate identities are not permitted.", nameof(values));
        return result;
    }
}
