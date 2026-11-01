using Twig.Core;

namespace Twig.Infrastructure.Persistence;

/// <summary>Native storage failure codes remain in the message; Core owns the result and error contract.</summary>
internal static class NativeResults
{
    internal static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    internal static Result<bool> Ok() => Result<bool>.Ok(true);
    internal static Result<T> Fail<T>(string message) => Result<T>.Fail(ErrorCode.StorageUnavailable, message);
    internal static Result<T> Fail<T>(Error error) => Result<T>.Fail(error);
    internal static Result<bool> Fail(string message) => Fail<bool>(message);
    internal static Result<bool> Fail(Error error) => Fail<bool>(error);
    internal static Result<T> Missing<T>() => Result<T>.Fail(ErrorCode.NotFound, "Native metadata was not found.");

    internal static T? Optional<T>(this Result<T?> result) where T : class
        => result.IsSuccess ? result.Value
            : result.Error.Code == ErrorCode.NotFound ? null
            : throw new InvalidOperationException(result.Error.Message);
}
