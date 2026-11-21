using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using HappyMix.Application.Abstractions.Quiet;
using HappyMix.Application.Quiet;
using MediatR;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Live notices: a DM to each member who asked for them, and a post in the group's notification channel when the
/// organizer turned that on. Messages use the community language; delivery problems are logged, never thrown.
/// A recipient with do-not-disturb on gets the DM when it ends, if the player is still in that game.
/// </summary>
internal sealed partial class DiscordLiveNotifier(DiscordSocketClient discord, EventNotificationSettings notificationSettings,
    IOptions<CommunityDefaults> community, ISender sender, ILogger<DiscordLiveNotifier> logger) : ILiveNotifier, IHeldMessageSender
{
    public string Kind => HeldKinds.Live;

    public async Task NotifyAsync(LiveNotice notice, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        string language = community.Value.LanguageCode;
        string text = Text(notice, language);
        foreach (ulong recipient in notice.DmRecipients)
        {
            var single = notice with { DmRecipients = [recipient], PostInChannel = false };
            string key = $"{notice.GroupId:N}:{notice.PlayerId}:{notice.Game.ToUpperInvariant()}";
            if ((await sender.Send(new HoldIfQuietCommand(recipient, Kind, key,
                    System.Text.Json.JsonSerializer.Serialize(single, HeldKinds.Json)), cancellationToken)).Value)
                continue;
            await SendDmAsync(notice.GuildId, recipient, text, cancellationToken);
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

    public async Task<HeldDelivery> SendAsync(HeldMessage message, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return HeldDelivery.RetryLater;
        if (System.Text.Json.JsonSerializer.Deserialize<LiveNotice>(message.Payload, HeldKinds.Json) is not { } notice)
            return HeldDelivery.Dropped;
        return await SendDmAsync(notice.GuildId, message.UserId, Text(notice, community.Value.LanguageCode), cancellationToken)
            ? HeldDelivery.Sent : HeldDelivery.Dropped;
    }

    private async Task<bool> SendDmAsync(ulong guildId, ulong recipient, string text, CancellationToken cancellationToken)
    {
        try
        {
            var user = await discord.Rest.GetUserAsync(recipient, new RequestOptions { CancelToken = cancellationToken });
            if (user is null)
                return false;
            var channel = await user.CreateDMChannelAsync(new RequestOptions { CancelToken = cancellationToken });
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
            return true;
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotSent(logger, guildId, recipient, error);
            return false;
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
