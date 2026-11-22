using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Stats;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Posts a match recap in the group's notification channel, in the community language. Members are named with
/// mentions that don't ping. Delivery problems are logged, never thrown.
/// </summary>
internal sealed partial class DiscordRecapPublisher(DiscordSocketClient discord, EventNotificationSettings notificationSettings,
    IOptions<CommunityDefaults> community, ILogger<DiscordRecapPublisher> logger) : IRecapPublisher
{
    public Task PublishAsync(ulong guildId, string groupName, GroupRecap recap, CancellationToken cancellationToken) =>
        PostAsync(guildId, recap.GroupId, Text(groupName, recap, community.Value.LanguageCode), cancellationToken);

    public Task PublishDigestAsync(ulong guildId, Guid groupId, string groupName, DateOnly weekStart,
        IReadOnlyList<HappyMix.Application.Stats.GroupDigest> digests, CancellationToken cancellationToken) =>
        PostAsync(guildId, groupId, DigestText(groupName, weekStart, digests, community.Value.LanguageCode), cancellationToken);

    private async Task PostAsync(ulong guildId, Guid groupId, string text, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected
            || await notificationSettings.GetEffectiveAsync(guildId, "group", groupId) is not { } effective || !effective.Route.Enabled
            || discord.GetGuild(guildId)?.GetTextChannel(effective.Route.Channel()) is not { } channel)
            return;
        try
        {
            await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotPosted(logger, guildId, channel.Id, error);
        }
    }

    /// <summary>
    /// "📅 Squad · week of 16 Nov", then per game: games together, wins and losses, the most played hero or map and the
    /// best performer.
    /// </summary>
    internal static string DigestText(string groupName, DateOnly weekStart, IReadOnlyList<HappyMix.Application.Stats.GroupDigest> digests, string language)
    {
        var culture = CultureInfo.GetCultureInfo(language is "uk" or "ru" ? language : "en");
        var lines = new List<string> { NotificationText.Get(language, "Digest_Title", Format.Sanitize(groupName), weekStart.ToString("d MMMM", culture)) };
        foreach (var digest in digests)
        {
            string line = $"**{(digest.Game == StatsGame.Dota2 ? "Dota 2" : "CS2")}**: "
                + NotificationText.Get(language, "Digest_Games", digest.Games, digest.Wins, digest.Losses);
            if (digest.MostPlayed is { } most)
                line += " · " + NotificationText.Get(language, "Digest_MostPlayed", Format.Sanitize(most), digest.MostPlayedCount);
            if (digest.Best is { } best)
                line += " · " + NotificationText.Get(language, "Digest_Best", $"<@{best.UserId}>", best.Wins, best.Games);
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// "🏆 Squad won a Dota 2 match together · 38 min", one line per member with their hero or score, and match links.
    /// </summary>
    internal static string Text(string groupName, GroupRecap recap, string language)
    {
        string game = recap.Game == StatsGame.Dota2 ? "Dota 2" : "CS2";
        string minutes = NotificationText.Get(language, "Live_Minutes", Math.Max(1, recap.DurationSeconds / 60));
        string key = recap.Won switch { true => "Recap_Won", false => "Recap_Lost", null => "Recap_Split" };
        var first = recap.Players[0];
        string map = recap.Game == StatsGame.Cs2 && first.Pick is { Length: > 0 } pick
            ? " · " + Format.Sanitize(pick) + (first.Score is { Length: > 0 } score ? $" ({Format.Sanitize(score)})" : "") : "";
        var lines = new List<string> { NotificationText.Get(language, key, Format.Sanitize(groupName), game) + map + " · " + minutes };
        lines.AddRange(recap.Players.Select(player =>
            $"<@{player.UserId}> " + (recap.Won is null ? (player.Won ? "✅ " : "❌ ") : "")
            + (recap.Game == StatsGame.Dota2 && player.Pick is { Length: > 0 } hero ? Format.Sanitize(hero) + " · " : "")
            + $"{player.Kills}/{player.Deaths}/{player.Assists}"
            + (player.Headshots is { } headshots ? " · " + NotificationText.Get(language, "Recap_Headshots", headshots.ToString("0", CultureInfo.InvariantCulture)) : "")));
        lines.Add(recap.Game == StatsGame.Dota2
            ? $"<https://www.opendota.com/matches/{recap.MatchId}> · <https://www.dotabuff.com/matches/{recap.MatchId}>"
            : $"<https://www.faceit.com/en/cs2/room/{Uri.EscapeDataString(recap.MatchId)}>");
        return string.Join("\n", lines);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match recap for server {GuildId} was not posted in channel {ChannelId}")]
    private static partial void LogNotPosted(ILogger logger, ulong guildId, ulong channelId, Exception error);
}
