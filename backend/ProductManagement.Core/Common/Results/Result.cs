using System.Diagnostics.CodeAnalysis;
using ProductManagement.Core.Common.Errors;

namespace ProductManagement.Core.Common.Results;

/// <summary>
/// The outcome of an operation that returns no value: success, or an <see cref="Errors.Error"/> saying why it failed.
/// </summary>
/// <remarks>
/// Expected failures (not found, validation, conflicts) are returned as values, not thrown. Exceptions are kept for
/// unexpected faults, which the API's global exception handler turns into HTTP 500 responses.
/// </remarks>
public class Result
{
    protected Result(Error? error) => Error = error;

    // [MemberNotNullWhen] tells the compiler that Error is not null once IsFailure is true (and vice versa).
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public Error? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    /// <summary>Lets a method simply <c>return SomeErrors.X();</c> where a <see cref="Result"/> is expected.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// The outcome of an operation that returns a <typeparamref name="TValue"/> when it succeeds.
/// </summary>
/// <remarks>
/// The implicit conversions let a method <c>return product;</c> or <c>return ProductErrors.NotFound(id);</c> without
/// wrapping either one by hand.
/// </remarks>
#pragma warning disable CA1000 // Static factory members on a generic result type are the idiomatic API here.
public class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue value)
        : base(null) => _value = value;

    private Result(Error error)
        : base(error)
    {
    }

    /// <summary>The value of a successful result. Accessing it on a failure is a programming error.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public static Result<TValue> Success(TValue value) => new(value);

    public static new Result<TValue> Failure(Error error) => new(error);

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure(error);
}
#pragma warning restore CA1000
