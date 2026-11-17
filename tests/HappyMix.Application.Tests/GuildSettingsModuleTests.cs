using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application;
using HappyMix.Bot.Interactions;
using HappyMix.Application.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HappyMix.Infrastructure.PandaScore;
using HappyMix.Bot.Configuration;

namespace HappyMix.Application.Tests;

public sealed class GuildSettingsModuleTests
{
    private static readonly string[] SettingsCommands =
        ["add-team", "broadcasts", "channel", "create-channel", "notifications", "organizer-role", "ping-role", "reminder", "remove-team", "reset-catalog", "reset-channel", "set", "show",
            "team-catalog", "teams", "timezone", "twitch"];

    [Fact]
    public async Task DiscordDiscoversSettingsCommandsWithAdministratorAndGuildPreconditions()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = new ServiceCollection().AddLogging().AddApplication()
            .AddSingleton(database.Services.GetRequiredService<IOptions<PandaScoreOptions>>())
            .AddSingleton(database.Services.GetRequiredService<IOptions<DiscordOptions>>())
            .AddSingleton<HappyMix.Bot.Services.NotificationChannels>()
            .BuildServiceProvider();
        var module = await interactions.AddModuleAsync<GuildSettingsModule>(services);

        var languages = module.SlashCommands.Single(command => command.Name == "broadcasts").Parameters
            .Single(parameter => parameter.Name == "language").Choices.Select(choice => choice.Name);
        Assert.Equal(["English", "Українська", "Русский"], languages);
        Assert.Equal(SettingsCommands, module.SlashCommands.Select(command => command.Name).Order());
        Assert.Equal(GuildPermission.Administrator, module.DefaultMemberPermissions);
        Assert.Contains(module.Preconditions.OfType<RequireUserPermissionAttribute>(),
            permission => permission.GuildPermission == GuildPermission.Administrator);
        Assert.Contains(module.Preconditions, condition => condition is RequireContextAttribute);
    }
}
