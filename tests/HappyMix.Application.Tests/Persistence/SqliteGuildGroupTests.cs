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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolvingAnInvitationConsumesOnlyTheRecipientsPendingToken(bool accept)
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IGuildGroupStore>();
        Guid token = Guid.NewGuid();
        Guid otherToken = Guid.NewGuid();
        await store.UpdateAsync(1, "Friday", _ =>
            new GuildGroup("Friday", 10, [10], [20, 30], new() { [20] = token, [30] = otherToken }));

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<IGuildGroupStore>();
        var pending = Assert.Single(await store.ListAsync(1));
        Assert.Equal(token, pending.InviteTokens![20]);
        Assert.Null(GroupInvitation.Resolve(pending, 99, accept));
        await store.UpdateAsync(1, "Friday", group => GroupInvitation.Resolve(group!, 20, accept));

        var resolved = Assert.Single(await store.ListAsync(1));
        Assert.Equal(accept, resolved.Members.Contains(20UL));
        Assert.Equal([30UL], resolved.Invites);
        Assert.Equal(otherToken, resolved.InviteTokens![30]);
        Assert.False(resolved.InviteTokens.ContainsKey(20));
        Assert.Null(GroupInvitation.Resolve(resolved, 20, accept));
    }

    [Fact]
    public async Task DiscordDiscoversTheGuildGroupCommands()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var database = await SqliteTestDatabase.CreateAsync();
        var module = await interactions.AddModuleAsync<GuildGroupModule>(database.Services);

        Assert.Equal(8, module.SlashCommands.Count);
        Assert.Contains(module.SlashCommands, command => command.Name == "invite");
        Assert.Contains(module.SlashCommands, command => command.Name == "join");
        Assert.Contains(module.SlashCommands, command => command.Name == "decline");
        var buttons = await interactions.AddModuleAsync<GroupInvitationModule>(database.Services);
        Assert.Single(buttons.ComponentCommands);
        Assert.Contains(module.Preconditions, condition => condition is RequireContextAttribute);
    }
}
