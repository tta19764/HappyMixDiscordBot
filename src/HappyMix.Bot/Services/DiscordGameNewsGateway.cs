using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.News;

namespace HappyMix.Bot.Services;

/// <summary>The Discord and Steam side of the game news commands: channels, roles and reading posts.</summary>
public sealed class DiscordGameNewsGateway(DiscordSocketClient discord, NotificationChannels notificationChannels, IHttpClientFactory clients)
    : IGameNewsGateway
{
    private const string DiscordFailed = "Discord could not finish the request. Check the bot’s permissions and role position, then try again.";

    public async Task<(ulong ChannelId, ulong RoleId)> CreateAsync(ulong guildId, ulong ownerId, uint appId, string name, CancellationToken ct)
    {
        IGuild guild = await GuildAsync(guildId);
        IRole? role = null;
        ITextChannel? channel = null;
        try
        {
            role = await guild.CreateRoleAsync($"News · {name}", GuildPermissions.None, isMentionable: false);
            channel = await notificationChannels.CreateAsync(guild, $"{GameNewsService.ChannelName(name)}-{appId}",
                $"Steam news for {name} · https://store.steampowered.com/app/{appId}/");
            var bot = await guild.GetCurrentUserAsync(CacheMode.AllowDownload);
            var permissions = bot.GetPermissions(channel);
            if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.MentionEveryone)
                throw new GameNewsException("HappyMix needs View Channel, Send Messages, Embed Links, and Mention Everyone in the news channel.");
            var owner = await guild.GetUserAsync(ownerId) ?? throw new GameNewsException(DiscordFailed);
            await owner.AddRoleAsync(role.Id);
            return (channel.Id, role.Id);
        }
        catch (Exception error) when (error is HttpException or GameNewsException)
        {
            try
            {
                if (channel is not null) await channel.DeleteAsync();
            }
            catch (HttpException) { }
            try
            {
                if (role is not null) await role.DeleteAsync();
            }
            catch (HttpException) { }
            throw error as GameNewsException ?? new GameNewsException(DiscordFailed, error);
        }
    }

    public async Task DeleteAsync(ulong guildId, ulong channelId, ulong roleId, CancellationToken ct)
    {
        IGuild guild = await discord.Rest.GetGuildAsync(guildId);
        try
        {
            if (await guild.GetTextChannelAsync(channelId) is { } channel)
                await channel.DeleteAsync();
            if (guild.GetRole(roleId) is { } role)
                await role.DeleteAsync();
        }
        catch (HttpException error) when (error.HttpCode == HttpStatusCode.NotFound) { }
        catch (HttpException error)
        {
            throw new GameNewsException(DiscordFailed, error);
        }
    }

    public async Task SetRoleAsync(ulong guildId, ulong userId, ulong roleId, bool add, CancellationToken ct)
    {
        IGuild guild = await GuildAsync(guildId);
        try
        {
            var member = await guild.GetUserAsync(userId) ?? throw new GameNewsException(DiscordFailed);
            if (add)
                await member.AddRoleAsync(roleId);
            else
                await member.RemoveRoleAsync(roleId);
        }
        catch (HttpException error)
        {
            throw new GameNewsException(DiscordFailed, error);
        }
    }

    public async Task<IReadOnlyList<GameNewsPost>> ReadPostsAsync(uint appId, long since, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient("SteamNews");
            return (await GameNewsService.ReadNewsAsync(client, appId, since, ct)).Select(post => new GameNewsPost(post.Gid, GameNewsService.Kind(post))).ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new GameNewsException("Steam didn't answer. Try again in a minute.", error);
        }
    }

    private async Task<IGuild> GuildAsync(ulong guildId) =>
        discord.GetGuild(guildId) as IGuild ?? await discord.Rest.GetGuildAsync(guildId) ?? throw new GameNewsException(DiscordFailed);
}
