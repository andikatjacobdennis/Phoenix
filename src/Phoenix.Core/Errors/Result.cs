using System.Diagnostics.CodeAnalysis;

namespace Phoenix.Core.Errors;

/// <summary>Outcome of an operation that either worked or produced a <see cref="PhoenixError"/>.</summary>
public readonly struct Result
{
    private Result(PhoenixError? error) => Error = error;

    public PhoenixError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(PhoenixError error) => new(error);

    public static implicit operator Result(PhoenixError error) => new(error);
}

/// <summary>Outcome of an operation that produces a value.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, PhoenixError? error)
    {
        _value = value;
        Error = error;
    }

    public PhoenixError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    /// <summary>The produced value. Only valid when <see cref="IsSuccess"/> is true.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result has no value: {Error}");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(PhoenixError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(PhoenixError error) => Failure(error);

    public Result ToResult() => IsSuccess ? Result.Success() : Result.Failure(Error);
}
