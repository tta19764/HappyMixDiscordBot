using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Guilds;

namespace HappyMix.Bot.Services;

/// <summary>Confirms with Discord that a user is a human member of a server right now (one REST call).</summary>
internal sealed class DiscordGuildMemberGateway(DiscordSocketClient client) : IGuildMemberGateway
{
    public async Task<bool> IsMemberAsync(ulong guildId, ulong userId) =>
        await GetHumanAsync(guildId, userId) is not null;

    public async Task<bool> IsAdministratorAsync(ulong guildId, ulong userId) =>
        (await GetHumanAsync(guildId, userId))?.GuildPermissions.Administrator == true;

    private async Task<Discord.Rest.RestGuildUser?> GetHumanAsync(ulong guildId, ulong userId)
    {
        try
        {
            var guild = await client.Rest.GetGuildAsync(guildId);
            var member = await guild.GetUserAsync(userId);
            return member is { IsBot: false } ? member : null;
        }
        catch (HttpException exception) when (exception.DiscordCode is DiscordErrorCode.UnknownMember or DiscordErrorCode.UnknownGuild)
        {
            return null;
        }
    }
}
