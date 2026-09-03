using HappyMix.Application.Abstractions;
using HappyMix.Application.Status.GetBotStatus;

namespace HappyMix.Application.Tests.Status;

public sealed class GetBotStatusHandlerTests
{
    [Fact]
    public async Task HandleReturnsCurrentUtcTime()
    {
        var expected = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var handler = new GetBotStatusQueryHandler(new StubClock(expected));

        var result = await handler.Handle(new GetBotStatusQuery(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.CheckedAtUtc);
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
