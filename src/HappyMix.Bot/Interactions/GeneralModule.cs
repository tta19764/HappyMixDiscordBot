using Discord.Interactions;
using HappyMix.Application.Status;

namespace HappyMix.Bot.Interactions;

public sealed class GeneralModule(GetBotStatusHandler getBotStatus) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("ping", "Checks whether HappyMix is online.")]
    public async Task PingAsync()
    {
        var status = getBotStatus.Handle();
        await RespondAsync($"Pong! HappyMix is online at {status.CheckedAtUtc:O}.", ephemeral: true);
    }
}
