using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Guilds;

namespace HappyMix.Bot.Services;

/// <summary>Confirms with Discord that a user is a human member of a server right now (one REST call).</summary>
internal sealed class DiscordGuildMemberGateway(DiscordSocketClient client) : IGuildMemberGateway
{
    public async Task<bool> IsMemberAsync(ulong guildId, ulong userId)
    {
        try
        {
            var guild = await client.Rest.GetGuildAsync(guildId);
            var member = await guild.GetUserAsync(userId);
            return member is { IsBot: false };
        }
        catch (HttpException exception) when (exception.DiscordCode is DiscordErrorCode.UnknownMember or DiscordErrorCode.UnknownGuild)
        {
            return false;
        }
    }
}
