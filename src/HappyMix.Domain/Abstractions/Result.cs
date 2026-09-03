using System.Diagnostics.CodeAnalysis;

namespace HappyMix.Domain.Abstractions;

public class Result
{
    protected Result(bool isSuccess, ErrorDescriptor error)
    {
        if (isSuccess == (error != ErrorDescriptor.None))
        {
            throw new ArgumentException("A successful result cannot contain an error and a failed result must contain one.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ErrorDescriptor Error { get; }

    public static Result Success() => new(true, ErrorDescriptor.None);

    public static Result Failure(ErrorDescriptor error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, ErrorDescriptor.None);

    public static Result<TValue> Failure<TValue>(ErrorDescriptor error) => new(default, false, error);

    public static Result<TValue> Create<TValue>(TValue? value) =>
        value is not null ? Success(value) : Failure<TValue>(ErrorDescriptor.NullValue);
}

public sealed class Result<TValue> : Result
{
    internal Result(TValue? value, bool isSuccess, ErrorDescriptor error)
        : base(isSuccess, error)
    {
        Value = value;
    }

    [NotNull]
    public TValue Value => IsSuccess
        ? field!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public static implicit operator Result<TValue>(TValue? value) => Create(value);
}
