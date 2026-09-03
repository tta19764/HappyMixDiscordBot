using Discord.Interactions;
using HappyMix.Application.Status.GetBotStatus;
using MediatR;

namespace HappyMix.Bot.Interactions;

public sealed class GeneralModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
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
