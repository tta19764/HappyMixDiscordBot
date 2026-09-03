using HappyMix.Application.Abstractions;
using HappyMix.Application.Status;

namespace HappyMix.Application.Tests.Status;

public sealed class GetBotStatusHandlerTests
{
    [Fact]
    public void HandleReturnsCurrentUtcTime()
    {
        var expected = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var handler = new GetBotStatusHandler(new StubClock(expected));

        var result = handler.Handle();

        Assert.Equal(expected, result.CheckedAtUtc);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
