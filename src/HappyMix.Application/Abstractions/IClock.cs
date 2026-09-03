namespace HappyMix.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
