using HappyMix.Application.Abstractions;
using HappyMix.Domain;

namespace HappyMix.Application.Status;

public sealed class GetBotStatusHandler(IClock clock)
{
    public BotStatus Handle() => new(clock.UtcNow);
}
