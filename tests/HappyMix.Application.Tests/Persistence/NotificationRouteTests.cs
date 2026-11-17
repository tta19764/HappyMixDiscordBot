using HappyMix.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class NotificationRouteTests
{
    [Fact]
    public async Task OverridesAndDeliveryReceiptsSurviveRestartAndRemainIsolated()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var routes = database.Services.GetRequiredService<NotificationRouteStore>();
        var target = Guid.NewGuid();
        await routes.SaveAsync(new("1", "group", target.ToString(), true, "321", "uk"));
        await routes.SaveAsync(new("1", "event", target.ToString(), false, "0", "en"));
        await routes.MarkSentAsync("receipt");
        await routes.MarkSentAsync("receipt");
        routes = (await database.OpenAsync()).GetRequiredService<NotificationRouteStore>();
        Assert.True((await routes.GetAsync(1, "group", target))!.Enabled);
        Assert.False((await routes.GetAsync(1, "event", target))!.Enabled);
        Assert.Null(await routes.GetAsync(2, "group", target));
        Assert.True(await routes.WasSentAsync("receipt"));
        await routes.ResetAsync(1, "event", target);
        Assert.Null(await routes.GetAsync(1, "event", target));
        Assert.NotNull(await routes.GetAsync(1, "group", target));
    }
}
