using Discord.Interactions;
using HappyMix.Application;
using HappyMix.Bot.Configuration;
using HappyMix.Bot.Interactions;
using HappyMix.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests;

public sealed class DiscordCommandContractTests
{
    [Fact]
    public async Task EveryCommandHasValidDiscordTextLengthsAndUnicode()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddApplication().AddInfrastructure(configuration).AddBot(configuration).BuildServiceProvider();
        var interactions = services.GetRequiredService<InteractionService>();
        await interactions.AddModulesAsync(typeof(GeneralModule).Assembly, services);
        var errors = new List<string>();
        foreach (var module in interactions.Modules)
        {
            foreach (var command in module.SlashCommands)
            {
                Check(command.Description, 100, command.Name + " description", errors);
                foreach (var parameter in command.Parameters)
                {
                    if (!string.IsNullOrEmpty(parameter.Description))
                        Check(parameter.Description, 100, command.Name + "/" + parameter.Name + " description", errors);
                    foreach (var choice in parameter.Choices)
                        Check(choice.Name, 100, command.Name + "/" + parameter.Name + " choice", errors);
                }
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static void Check(string value, int limit, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Contains('\uFFFD'))
            errors.Add($"{path}: invalid text length or Unicode ({value.Length} characters)");
    }
}
