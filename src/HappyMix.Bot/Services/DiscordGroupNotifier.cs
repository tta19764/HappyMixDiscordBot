using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Group notices by direct message, in the server's community language, with a link to the group page when the
/// dashboard has a public address. People with closed DMs simply don't get them; the action itself already happened.
/// </summary>
internal sealed partial class DiscordGroupNotifier(DiscordSocketClient discord, IConfiguration configuration, IOptions<CommunityDefaults> community,
    ILogger<DiscordGroupNotifier> logger) : IGroupNotifier
{
    public async Task NotifyAsync(GroupNotice notice, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        string language = community.Value.LanguageCode;
        string server = Format.Sanitize(discord.GetGuild(notice.GuildId)?.Name ?? "");
        string group = Format.Sanitize(notice.GroupName);
        string actor = $"<@{notice.ActorId}>";
        string text = notice.Kind switch
        {
            GroupNoticeKind.JoinRequested => NotificationText.Get(language, "Groups_Dm_JoinRequested", actor, group, server),
            GroupNoticeKind.JoinApproved => NotificationText.Get(language, "Groups_Dm_JoinApproved", group, server),
            GroupNoticeKind.JoinDeclined => NotificationText.Get(language, "Groups_Dm_JoinDeclined", group, server),
            GroupNoticeKind.GameSuggested => NotificationText.Get(language, "Groups_Dm_GameSuggested", actor, Format.Sanitize(notice.Detail ?? ""), group),
            _ => NotificationText.Get(language, "Groups_Dm_SuggestionAccepted", Format.Sanitize(notice.Detail ?? ""), group)
        };
        if (Link(notice) is { } link)
            text += "\n" + link;
        try
        {
            var user = await discord.Rest.GetUserAsync(notice.RecipientId, new RequestOptions { CancelToken = cancellationToken });
            if (user is null)
                return;
            var channel = await user.CreateDMChannelAsync(new RequestOptions { CancelToken = cancellationToken });
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
            LogSent(logger, notice.Kind, notice.GuildId, notice.RecipientId);
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotSent(logger, notice.Kind, notice.GuildId, notice.RecipientId, error.DiscordCode?.ToString() ?? error.HttpCode.ToString());
        }
    }

    private string? Link(GroupNotice notice) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, $"/groups/{notice.GuildId}/{notice.GroupId}").ToString()
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Group notice {Kind} in server {GuildId} sent to user {UserId}")]
    private static partial void LogSent(ILogger logger, GroupNoticeKind kind, ulong guildId, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Group notice {Kind} in server {GuildId} couldn't reach user {UserId} ({Reason}); their DMs may be closed")]
    private static partial void LogNotSent(ILogger logger, GroupNoticeKind kind, ulong guildId, ulong userId, string reason);
}
