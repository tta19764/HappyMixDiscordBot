using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Live notices: a DM to each member who asked for them, and a post in the group's notification channel when the
/// organizer turned that on. Messages use the community language; delivery problems are logged, never thrown.
/// </summary>
internal sealed partial class DiscordLiveNotifier(DiscordSocketClient discord, EventNotificationSettings notificationSettings,
    IOptions<CommunityDefaults> community, ILogger<DiscordLiveNotifier> logger) : ILiveNotifier
{
    public async Task NotifyAsync(LiveNotice notice, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        string language = community.Value.LanguageCode;
        string text = Text(notice, language);
        foreach (ulong recipient in notice.DmRecipients)
        {
            try
            {
                var user = await discord.Rest.GetUserAsync(recipient, new RequestOptions { CancelToken = cancellationToken });
                if (user is null)
                    continue;
                var channel = await user.CreateDMChannelAsync(new RequestOptions { CancelToken = cancellationToken });
                await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
            }
            catch (Discord.Net.HttpException error)
            {
                LogNotSent(logger, notice.GuildId, recipient, error);
            }
        }

        if (notice.PostInChannel && await notificationSettings.GetEffectiveAsync(notice.GuildId, "group", notice.GroupId) is { } effective
            && effective.Route.Enabled && discord.GetGuild(notice.GuildId)?.GetTextChannel(effective.Route.Channel()) is { } groupChannel)
        {
            try
            {
                await groupChannel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
            }
            catch (Discord.Net.HttpException error)
            {
                LogChannelFailed(logger, notice.GuildId, groupChannel.Id, error);
            }
        }
    }

    /// <summary>"🎮 @Kai started Counter-Strike 2 · Friday Night Frags" and who else wants to play it.</summary>
    internal static string Text(LiveNotice notice, string language)
    {
        string text = NotificationText.Get(language, "Live_Started", $"<@{notice.PlayerId}>", Format.Sanitize(notice.Game), Format.Sanitize(notice.GroupName));
        if (notice.AlsoWant.Count > 0)
            text += "\n" + NotificationText.Get(language, "Live_AlsoWant", string.Join(", ", notice.AlsoWant.Take(10).Select(id => $"<@{id}>")));
        return text;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live notice in server {GuildId} couldn't reach user {UserId}; their DMs may be closed")]
    private static partial void LogNotSent(ILogger logger, ulong guildId, ulong userId, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live notice in server {GuildId} couldn't be posted in channel {ChannelId}; check the bot's access to it")]
    private static partial void LogChannelFailed(ILogger logger, ulong guildId, ulong channelId, Exception error);
}
