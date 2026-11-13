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
            Use `/availability add` to save a window. Enter both dates and times with an offset, such as `2026-11-13 20:00 +02:00`.
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
