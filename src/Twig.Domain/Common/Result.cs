namespace Twig.Domain.Common;

/// <summary>
/// Represents the outcome of a domain operation that does not return a value.
/// </summary>
public readonly record struct Result
{
    public bool IsSuccess { get; }
    public string Error { get; }

    private Result(bool isSuccess, string error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Ok() => new(true, string.Empty);
    public static Result Fail(string error) => new(false, error);

    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    public static Result<T> Fail<T>(string error) => Result<T>.Fail(error);
}

/// <summary>
/// Represents the outcome of a domain operation that returns a value of type <typeparamref name="T"/>.
/// </summary>
public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    private readonly T _value;
    public string Error { get; }

    private Result(bool isSuccess, T value, string error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    /// <summary>
    /// Gets the result value. Throws <see cref="InvalidOperationException"/> if the result is a failure.
    /// Always check <see cref="IsSuccess"/> before accessing this property.
    /// </summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"Cannot access Value on a failed result. Error: {Error}");

    public static Result<T> Ok(T value) => new(true, value, string.Empty);
    public static Result<T> Fail(string error) => new(false, default!, error);
}
