namespace HappyMix.Domain.Abstractions;

public sealed record ErrorDescriptor(string Code, string Description)
{
    public static ErrorDescriptor None { get; } = new(string.Empty, string.Empty);

    public static ErrorDescriptor NullValue { get; } = new(
        "Error.NullValue",
        "A required value was null.");
}
