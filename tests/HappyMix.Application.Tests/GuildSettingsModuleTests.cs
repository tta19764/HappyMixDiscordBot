using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Bot.Interactions;
using HappyMix.Application.Tests.Persistence;

namespace HappyMix.Application.Tests;

public sealed class GuildSettingsModuleTests
{
    [Fact]
    public async Task DiscordDiscoversSettingsCommandsWithAdministratorAndGuildPreconditions()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var database = await SqliteTestDatabase.CreateAsync();
        var module = await interactions.AddModuleAsync<GuildSettingsModule>(database.Services);

        Assert.Contains(module.SlashCommands, command => command.Name == "show");
        Assert.Contains(module.SlashCommands, command => command.Name == "set");
        Assert.Contains(module.Preconditions.OfType<RequireUserPermissionAttribute>(),
            permission => permission.GuildPermission == GuildPermission.Administrator);
        Assert.Contains(module.Preconditions, condition => condition is RequireContextAttribute);
    }
}
