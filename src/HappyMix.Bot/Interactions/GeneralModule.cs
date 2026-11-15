using Discord.Interactions;
using HappyMix.Application.Status.GetBotStatus;
using MediatR;

namespace HappyMix.Bot.Interactions;

public sealed class GeneralModule(ISender sender, InteractionService interactions, IConfiguration configuration) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Shows a private guide to the commands available in this server.</summary>
    [SlashCommand("help", "Show how to use HappyMix.")]
    public async Task HelpAsync(string? topic = null)
    {
        string guide = "";
        if (Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) && origin.Scheme == "https")
            guide = $"\n[HappyMix feature guide]({new Uri(origin, "/info")})";
        var catalog = CommandHelp.Read(interactions);
        string key = string.Join(' ', (topic ?? "").Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var exact = catalog.FirstOrDefault(command => command.Path.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            string options = string.Join("\n", exact.Options.Select(option =>
                $"• `{option.Name}` ({(option.IsRequired ? "required" : "optional")}) — {option.Description}"));
            await RespondAsync($"**/{exact.Path}**\n{exact.Description}\n{exact.Flow}\n{options}{guide}", ephemeral: true);
            return;
        }

        var matches = catalog.Where(command => key.Length == 0 || command.Path.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            await RespondAsync("No matching command. Run /help to see all topics.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        foreach (var module in matches.GroupBy(command => command.Path.Contains(' ') ? command.Path.Split(' ')[0] : "general"))
        {
            string heading = $"**{module.Key}**\n{CommandHelp.Flow(module.Key)}\n";
            var lines = module.Select(command => key.Length == 0 ? $"`/{command.Path}`" : $"`/{command.Path}` — {command.Description}");
            await FollowupAsync(heading + string.Join(key.Length == 0 ? " · " : "\n", lines), ephemeral: true);
        }
    }

    [SlashCommand("ping", "Checks whether HappyMix is online.")]
    public async Task PingAsync()
    {
        var result = await sender.Send(new GetBotStatusQuery());

        if (result.IsFailure)
        {
            await RespondAsync(result.Error.Description, ephemeral: true);
            return;
        }

        await RespondAsync($"Pong! HappyMix is online at {result.Value.CheckedAtUtc:O}.", ephemeral: true);
    }
}
