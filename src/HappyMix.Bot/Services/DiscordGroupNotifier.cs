using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Quiet;
using HappyMix.Application.Quiet;
using MediatR;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Group notices by direct message, in the server's community language, with a link to the group page when the
/// dashboard has a public address. People with closed DMs simply don't get them; the action itself already happened.
/// During the recipient's do-not-disturb the notice waits, and goes out afterwards only if it still applies (the
/// request is still open, the suggestion still waiting, and so on).
/// </summary>
internal sealed partial class DiscordGroupNotifier(DiscordSocketClient discord, IConfiguration configuration, IOptions<CommunityDefaults> community,
    ISender sender, ILogger<DiscordGroupNotifier> logger) : IGroupNotifier, IHeldMessageSender
{
    public string Kind => HeldKinds.Group;

    public async Task NotifyAsync(GroupNotice notice, CancellationToken cancellationToken)
    {
        string key = $"{notice.GroupId:N}:{notice.Kind}:{notice.ActorId}:{notice.SuggestionId:N}";
        if ((await sender.Send(new HoldIfQuietCommand(notice.RecipientId, Kind, key,
                System.Text.Json.JsonSerializer.Serialize(notice, HeldKinds.Json)), cancellationToken)).Value)
            return;
        await DeliverAsync(notice, cancellationToken);
    }

    public async Task<HeldDelivery> SendAsync(HeldMessage message, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return HeldDelivery.RetryLater;
        if (System.Text.Json.JsonSerializer.Deserialize<GroupNotice>(message.Payload, HeldKinds.Json) is not { } notice)
            return HeldDelivery.Dropped;
        return await DeliverAsync(notice, cancellationToken) ? HeldDelivery.Sent : HeldDelivery.Dropped;
    }

    private async Task<bool> DeliverAsync(GroupNotice notice, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return false;
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
                return false;
            var channel = await user.CreateDMChannelAsync(new RequestOptions { CancelToken = cancellationToken });
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, components: Buttons(notice, language),
                options: new RequestOptions { CancelToken = cancellationToken });
            LogSent(logger, notice.Kind, notice.GuildId, notice.RecipientId);
            return true;
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotSent(logger, notice.Kind, notice.GuildId, notice.RecipientId, error.DiscordCode?.ToString() ?? error.HttpCode.ToString());
            return false;
        }
    }

    /// <summary>
    /// The organizer answers from the message itself: Let in / Decline for a join request, Add / Dismiss for a game
    /// suggestion. <see cref="Interactions.GroupInvitationModule"/> handles the buttons with the same commands as the dashboard.
    /// </summary>
    internal static MessageComponent? Buttons(GroupNotice notice, string language) => notice.Kind switch
    {
        GroupNoticeKind.JoinRequested => new ComponentBuilder()
            .WithButton(NotificationText.Get(language, "Groups_Approve"), $"group-request:{notice.GuildId}:{notice.GroupId:N}:{notice.ActorId}:1", ButtonStyle.Success)
            .WithButton(NotificationText.Get(language, "Decline"), $"group-request:{notice.GuildId}:{notice.GroupId:N}:{notice.ActorId}:0", ButtonStyle.Secondary)
            .Build(),
        GroupNoticeKind.GameSuggested when notice.SuggestionId is { } suggestion => new ComponentBuilder()
            .WithButton(NotificationText.Get(language, "Groups_AddSuggested"), $"group-suggestion:{notice.GuildId}:{notice.GroupId:N}:{suggestion:N}:1", ButtonStyle.Success)
            .WithButton(NotificationText.Get(language, "Groups_Dismiss"), $"group-suggestion:{notice.GuildId}:{notice.GroupId:N}:{suggestion:N}:0", ButtonStyle.Secondary)
            .Build(),
        _ => null
    };

    private string? Link(GroupNotice notice) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, $"/groups/{notice.GuildId}/{notice.GroupId}").ToString()
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Group notice {Kind} in server {GuildId} sent to user {UserId}")]
    private static partial void LogSent(ILogger logger, GroupNoticeKind kind, ulong guildId, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Group notice {Kind} in server {GuildId} couldn't reach user {UserId} ({Reason}); their DMs may be closed")]
    private static partial void LogNotSent(ILogger logger, GroupNoticeKind kind, ulong guildId, ulong userId, string reason);
}
