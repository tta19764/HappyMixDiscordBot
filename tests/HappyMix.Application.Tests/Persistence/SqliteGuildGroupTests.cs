using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Bot.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteGuildGroupTests
{
    [Fact]
    public async Task GroupsPersistAcrossRestartAndAreIsolatedByGuild()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IGuildGroupStore>();
        await store.UpdateAsync(1, "Friday", _ => new GuildGroup("Friday", 10, [10], [20]));
        await store.UpdateAsync(2, "Friday", _ => new GuildGroup("Friday", 30, [30], []));
        await store.UpdateAsync(1, "friday", group => group! with { Members = [10, 20], Invites = [] });

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<IGuildGroupStore>();
        var first = Assert.Single(await store.ListAsync(1));
        Assert.Equal([10UL, 20UL], first.Members);
        Assert.Empty(first.Invites);
        Assert.Equal(30UL, Assert.Single(await store.ListAsync(2)).OrganizerId);

        await store.UpdateAsync(1, "FRIDAY", _ => null);
        Assert.Empty(await store.ListAsync(1));
        Assert.Single(await store.ListAsync(2));
    }

    [Fact]
    public async Task DiscordDiscoversTheGuildGroupCommands()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var database = await SqliteTestDatabase.CreateAsync();
        var module = await interactions.AddModuleAsync<GuildGroupModule>(database.Services);

        Assert.Equal(7, module.SlashCommands.Count);
        Assert.Contains(module.SlashCommands, command => command.Name == "invite");
        Assert.Contains(module.SlashCommands, command => command.Name == "join");
        Assert.Contains(module.Preconditions, condition => condition is RequireContextAttribute);
    }
}
