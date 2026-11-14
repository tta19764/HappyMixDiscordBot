using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using MediatR;
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
        await store.UpdateAsync(1, "friday", group => group! with
        {
            Members = [10, 20],
            Invites = [],
            Description = "Friday co-op",
            RoleId = 123
        });

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<IGuildGroupStore>();
        var first = Assert.Single(await store.ListAsync(1));
        Assert.Equal([10UL, 20UL], first.Members);
        Assert.Empty(first.Invites);
        Assert.Equal("Friday co-op", first.Description);
        Assert.Equal(123UL, first.RoleId);
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
        using var services = CreateApplicationServices(store, reopened.GetRequiredService<IGuildSettingsStore>());
        var sender = services.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new ResolveGroupInviteCommand(new(1, 99, false), null, token, accept))).IsFailure);
        var command = new ResolveGroupInviteCommand(new(1, 20, false), null, token, accept);
        Assert.True((await sender.Send(command)).IsSuccess);

        var resolved = Assert.Single(await store.ListAsync(1));
        Assert.Equal(accept, resolved.Members.Contains(20UL));
        Assert.Equal([30UL], resolved.Invites);
        Assert.Equal(otherToken, resolved.InviteTokens![30]);
        Assert.False(resolved.InviteTokens.ContainsKey(20));
        Assert.True((await sender.Send(command)).IsFailure);
    }

    [Fact]
    public async Task DiscordDiscoversTheGuildGroupCommands()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateApplicationServices(database.Services.GetRequiredService<IGuildGroupStore>(),
            database.Services.GetRequiredService<IGuildSettingsStore>());
        var module = await interactions.AddModuleAsync<GuildGroupModule>(services);

        Assert.Equal(11, module.SlashCommands.Count);
        Assert.Contains(module.SlashCommands, command => command.Name == "repair-roles");
        Assert.Single(module.ComponentCommands);
        Assert.Contains(module.SlashCommands, command => command.Name == "me");
        Assert.Contains(module.SlashCommands, command => command.Name == "edit-description");
        Assert.Contains(module.SlashCommands, command => command.Name == "invite");
        Assert.Contains(module.SlashCommands, command => command.Name == "join");
        Assert.Contains(module.SlashCommands, command => command.Name == "decline");
        var buttons = await interactions.AddModuleAsync<GroupInvitationModule>(services);
        Assert.Single(buttons.ComponentCommands);
        Assert.Contains(module.Preconditions, condition => condition is RequireContextAttribute);
    }

    private static ServiceProvider CreateApplicationServices(IGuildGroupStore store, IGuildSettingsStore settings) =>
        new ServiceCollection().AddLogging().AddSingleton(store).AddSingleton(settings)
            .AddSingleton<IGroupRoleGateway, TestGroupRoleGateway>().AddApplication().BuildServiceProvider();

    private sealed class TestGroupRoleGateway : IGroupRoleGateway
    {
        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) =>
            Task.FromResult<IReadOnlyCollection<ulong>>([]);

        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(123UL);

        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;

        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) =>
            Task.CompletedTask;
    }
}
