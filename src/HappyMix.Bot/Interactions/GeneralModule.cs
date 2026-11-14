using Discord.Interactions;
using HappyMix.Application.Status.GetBotStatus;
using MediatR;

namespace HappyMix.Bot.Interactions;

public sealed class GeneralModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Shows a private guide to the commands available in this server.</summary>
    [SlashCommand("help", "Show how to use HappyMix.")]
    public Task HelpAsync()
    {
        const string guide = """
            **HappyMix commands**

            **Gaming groups**
            `/group create` saves a group. Organizers can use `/group invite` to send an invitation by DM.
            Accept or decline in the DM, or use `/group list` to find the invitation and `/group join` or `/group decline` to respond.
            Use `/group leave` to leave. Organizers can use `/group remove` to remove a member or revoke an invite, and `/group delete` to close the group.

            **Availability**
            Use `/availability set today start:17 end:00:30` for tonight, or `/availability set timetable` to set a recurring day.
            Times use the server time zone unless you supply an offset like `+02:00`. An earlier end time means tomorrow.
            Use `/availability timetable` to view weekly availability and `/availability clear-day` to remove a day.
            Use `/availability list` to find the window ID for `/availability edit` or `/availability remove`.

            **Esports settings · Manage Server or Administrator**
            Use `/settings show` to review settings. Set the destination with `/settings channel`, followed teams with `/settings teams`,
            notification types with `/settings notifications`, and preferred stream channels with `/settings twitch`.

            `/ping` checks whether the bot is online.
            """;
        return RespondAsync(guide, ephemeral: true);
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
