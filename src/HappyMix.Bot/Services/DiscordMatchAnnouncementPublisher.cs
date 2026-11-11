using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Bot.Configuration;
using HappyMix.Domain.Esports;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>Formats CS2 and Dota 2 announcements for the configured Discord channel.</summary>
/// <param name="client">Connected Discord client.</param>
/// <param name="options">Announcement destination settings.</param>
internal sealed class DiscordMatchAnnouncementPublisher(
    DiscordSocketClient client,
    IOptions<DiscordOptions> options,
    GuildSettings? settings = null) : IMatchAnnouncementPublisher
{
    private readonly DiscordOptions _options = options.Value;
    private ulong ChannelId => settings?.ChannelId ?? _options.AnnouncementChannelId;

    internal DiscordMatchAnnouncementPublisher ForGuild(GuildSettings destination) =>
        new(client, options, destination);

    /// <inheritdoc />
    public async Task PublishDailyScheduleAsync(
        DateOnly matchDate,
        IReadOnlyCollection<EsportsMatch> matches,
        CancellationToken cancellationToken)
    {
        foreach (Embed embed in BuildDailyScheduleEmbeds(matchDate, matches))
        {
            await SendAsync(embed, cancellationToken);
        }
    }

    /// <summary>Builds bounded pages without merging tournaments across disciplines.</summary>
    internal static IReadOnlyList<Embed> BuildDailyScheduleEmbeds(
        DateOnly matchDate,
        IReadOnlyCollection<EsportsMatch> matches)
    {
        if (matches.Count == 0)
        {
            return BuildTournamentScheduleEmbeds(matchDate, [], "Followed main teams - CS2 / Dota 2");
        }

        return matches.OrderBy(match => match.BeginAt)
            .GroupBy(match => (match.Game, match.TournamentId,
                Fallback: match.TournamentId is null ? $"{match.LeagueName} • {match.TournamentName}" : string.Empty))
            .SelectMany(group => BuildTournamentScheduleEmbeds(matchDate, group.ToArray(),
                $"{group.First().Game.DisplayName()} • {group.First().LeagueName} • {group.First().TournamentName}"))
            .ToArray();
    }

    private static Embed[] BuildTournamentScheduleEmbeds(
        DateOnly matchDate,
        EsportsMatch[] matches,
        string tournamentTitle)
    {
        const int descriptionLimit = 4096;
        IEnumerable<string> lines = matches.Length == 0
            ? ["No followed main-team CS2 or Dota 2 matches are scheduled today."]
            : matches.Select(FormatScheduledMatch);
        var descriptions = new List<string>();
        string current = string.Empty;
        foreach (string line in lines)
        {
            if (current.Length > 0 && current.Length + 1 + line.Length > descriptionLimit)
            {
                descriptions.Add(current);
                current = string.Empty;
            }

            // Preserve even an unusually long entry without exceeding the embed limit.
            string remaining = line;
            while (remaining.Length > descriptionLimit)
            {
                int length = char.IsHighSurrogate(remaining[descriptionLimit - 1])
                    ? descriptionLimit - 1
                    : descriptionLimit;
                descriptions.Add(remaining[..length]);
                remaining = remaining[length..];
            }

            current = current.Length == 0 ? remaining : $"{current}\n{remaining}";
        }

        if (current.Length > 0)
        {
            descriptions.Add(current);
        }

        return descriptions.Select((description, index) => new EmbedBuilder()
            .WithTitle(tournamentTitle.Length <= 256 ? tournamentTitle : tournamentTitle[..253] + "...")
            .WithDescription(description)
            .WithFooter($"{matchDate:yyyy-MM-dd} • Main teams • Page {index + 1}/{descriptions.Count}")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .Build()).ToArray();
    }

    /// <inheritdoc />
    public Task PublishStartingSoonAsync(
        EsportsMatch match,
        CancellationToken cancellationToken)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • Match starts within {settings?.Preferences?.ReminderMinutes ?? 30} minutes")
            .WithDescription(FormatMatchDetails(match))
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();

        ulong? role = match.Game == Esport.CounterStrike2
            ? settings?.Preferences?.CounterStrikePingRoleId : settings?.Preferences?.Dota2PingRoleId;
        return SendAsync(embed, cancellationToken, role);
    }

    /// <inheritdoc />
    public Task PublishMapResultAsync(
        EsportsMatch match,
        EsportsGame game,
        CancellationToken cancellationToken)
        => SendAsync(BuildGameResultEmbed(match, game), cancellationToken);

    /// <summary>Formats an individual result using map terminology for CS2 and game terminology for Dota 2.</summary>
    /// <param name="match">The parent series and discipline.</param>
    /// <param name="game">The completed map/game.</param>
    /// <returns>The result embed without sending it to Discord.</returns>
    internal static Embed BuildGameResultEmbed(EsportsMatch match, EsportsGame game)
    {
        string winner = GetTeamName(match, game.WinnerId) ?? "Unknown winner";
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • {(match.Game == Esport.CounterStrike2 ? "Map" : "Game")} {game.Position} finished")
            .WithDescription($"**{winner}** won the {(match.Game == Esport.CounterStrike2 ? "map" : "game")}.\n{FormatScore(match)}")
            .WithFooter($"{match.LeagueName} • {match.TournamentName}")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .Build();

        return embed;
    }

    /// <inheritdoc />
    public Task PublishMatchResultAsync(
        EsportsMatch match,
        CancellationToken cancellationToken)
    {
        string winner = GetTeamName(match, match.WinnerId) ?? "Winner unavailable";
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • Match finished")
            .WithDescription($"**{winner}** won the match.\n{FormatScore(match)}")
            .WithFooter($"{match.LeagueName} • {match.TournamentName}")
            .WithColor(Color.Green)
            .WithCurrentTimestamp()
            .Build();

        return SendAsync(embed, cancellationToken);
    }

    internal async Task<ulong?> GetLegacyGuildIdAsync(CancellationToken cancellationToken) =>
        (await ResolveChannelAsync(_options.AnnouncementChannelId, cancellationToken) as IGuildChannel)?.GuildId;

    private async Task SendAsync(Embed embed, CancellationToken cancellationToken, ulong? roleId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ChannelId == 0)
        {
            throw new InvalidOperationException(
                "A Discord announcement channel is required. Set Discord__AnnouncementChannelId.");
        }

        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        var resolvedChannel = await ResolveChannelAsync(ChannelId, cancellationToken);
        if (resolvedChannel is not IMessageChannel channel ||
            settings is not null && (resolvedChannel as IGuildChannel)?.GuildId != settings.GuildId)
        {
            throw new InvalidOperationException($"Discord channel {ChannelId} is not an accessible destination for this server.");
        }

        var mentions = roleId is { } id
            ? new AllowedMentions { RoleIds = [id] }
            : AllowedMentions.None;
        await channel.SendMessageAsync(text: roleId is { } role ? $"<@&{role}>" : null,
            embed: embed, allowedMentions: mentions, options: requestOptions);
    }

    private async Task<IChannel?> ResolveChannelAsync(ulong channelId, CancellationToken cancellationToken)
    {
        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        IChannel? resolvedChannel = client.GetChannel(channelId);
        if (resolvedChannel is null)
        {
            // Quartz can run before the gateway has populated its channel cache.
            try
            {
                resolvedChannel = await ((IDiscordClient)client.Rest).GetChannelAsync(
                    channelId, options: requestOptions);
            }
            catch (Discord.Net.HttpException exception) when (
                exception.HttpCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"Discord channel {channelId} could not be accessed. " +
                    "Verify the announcement channel ID, that the bot belongs to its server, " +
                    "and the bot has View Channel permission.", exception);
            }
        }

        return resolvedChannel;
    }

    private static string FormatScheduledMatch(EsportsMatch match)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:t>"
            : "TBD";
        return $"{start} — **{GetMatchup(match)}** · {match.LeagueName}";
    }

    private static string FormatMatchDetails(EsportsMatch match)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:F>"
            : "Start time TBD";
        return $"**{GetMatchup(match)}**\n{match.LeagueName} · {match.TournamentName}\n{start}";
    }

    private static string GetMatchup(EsportsMatch match) => match.Opponents.Count >= 2
        ? $"{match.Opponents.ElementAt(0).Name} vs {match.Opponents.ElementAt(1).Name}"
        : match.Name;

    private static string FormatScore(EsportsMatch match)
    {
        if (match.Opponents.Count < 2)
        {
            return match.Name;
        }

        var teams = match.Opponents.Take(2).ToArray();
        int firstScore = match.Results.FirstOrDefault(result => result.TeamId == teams[0].Id)?.Score ?? 0;
        int secondScore = match.Results.FirstOrDefault(result => result.TeamId == teams[1].Id)?.Score ?? 0;
        return $"**{teams[0].Name}** {firstScore}–{secondScore} **{teams[1].Name}**";
    }

    private static string? GetTeamName(EsportsMatch match, long? teamId) =>
        match.Opponents.FirstOrDefault(opponent => opponent.Id == teamId)?.Name;
}
