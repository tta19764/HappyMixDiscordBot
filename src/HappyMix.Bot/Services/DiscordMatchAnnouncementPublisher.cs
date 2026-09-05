using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Bot.Configuration;
using HappyMix.Domain.CounterStrike;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

internal sealed class DiscordMatchAnnouncementPublisher(
    DiscordSocketClient client,
    IOptions<DiscordOptions> options) : IMatchAnnouncementPublisher
{
    private readonly DiscordOptions _options = options.Value;

    public async Task PublishDailyScheduleAsync(
        DateOnly matchDate,
        IReadOnlyCollection<CounterStrikeMatch> matches,
        CancellationToken cancellationToken)
    {
        foreach (Embed embed in BuildDailyScheduleEmbeds(matchDate, matches))
        {
            await SendAsync(embed, cancellationToken);
        }
    }

    internal static IReadOnlyList<Embed> BuildDailyScheduleEmbeds(
        DateOnly matchDate,
        IReadOnlyCollection<CounterStrikeMatch> matches)
    {
        if (matches.Count == 0)
        {
            return BuildTournamentScheduleEmbeds(matchDate, [], "Tier 1 Counter-Strike 2 matches");
        }

        return matches.OrderBy(match => match.BeginAt)
            .GroupBy(match => (match.TournamentId,
                Fallback: match.TournamentId is null ? $"{match.LeagueName} • {match.TournamentName}" : string.Empty))
            .SelectMany(group => BuildTournamentScheduleEmbeds(matchDate, group.ToArray(),
                $"{group.First().LeagueName} • {group.First().TournamentName}"))
            .ToArray();
    }

    private static Embed[] BuildTournamentScheduleEmbeds(
        DateOnly matchDate,
        CounterStrikeMatch[] matches,
        string tournamentTitle)
    {
        const int descriptionLimit = 4096;
        IEnumerable<string> lines = matches.Length == 0
            ? ["No tier 1 Counter-Strike 2 matches are scheduled today."]
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
            .WithFooter($"{matchDate:yyyy-MM-dd} • Tier 1 • Page {index + 1}/{descriptions.Count}")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .Build()).ToArray();
    }

    public Task PublishStartingSoonAsync(
        CounterStrikeMatch match,
        CancellationToken cancellationToken)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Match starts in 30 minutes")
            .WithDescription(FormatMatchDetails(match))
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();

        return SendAsync(embed, cancellationToken);
    }

    public Task PublishMapResultAsync(
        CounterStrikeMatch match,
        CounterStrikeGame game,
        CancellationToken cancellationToken)
    {
        string winner = GetTeamName(match, game.WinnerId) ?? "Unknown winner";
        var embed = new EmbedBuilder()
            .WithTitle($"Map {game.Position} finished")
            .WithDescription($"**{winner}** won the map.\n{FormatScore(match)}")
            .WithFooter($"{match.LeagueName} • {match.TournamentName}")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .Build();

        return SendAsync(embed, cancellationToken);
    }

    public Task PublishMatchResultAsync(
        CounterStrikeMatch match,
        CancellationToken cancellationToken)
    {
        string winner = GetTeamName(match, match.WinnerId) ?? "Winner unavailable";
        var embed = new EmbedBuilder()
            .WithTitle("Match finished")
            .WithDescription($"**{winner}** won the match.\n{FormatScore(match)}")
            .WithFooter($"{match.LeagueName} • {match.TournamentName}")
            .WithColor(Color.Green)
            .WithCurrentTimestamp()
            .Build();

        return SendAsync(embed, cancellationToken);
    }

    private async Task SendAsync(Embed embed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.AnnouncementChannelId == 0)
        {
            throw new InvalidOperationException(
                "A Discord announcement channel is required. Set Discord__AnnouncementChannelId.");
        }

        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        IChannel? resolvedChannel = client.GetChannel(_options.AnnouncementChannelId);
        if (resolvedChannel is null)
        {
            // Quartz can run before the gateway has populated its channel cache.
            try
            {
                resolvedChannel = await ((IDiscordClient)client.Rest).GetChannelAsync(
                    _options.AnnouncementChannelId, options: requestOptions);
            }
            catch (Discord.Net.HttpException exception) when (
                exception.HttpCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"Discord channel {_options.AnnouncementChannelId} could not be accessed. " +
                    "Verify Discord__AnnouncementChannelId is a channel ID, the bot belongs to its server, " +
                    "and the bot has View Channel permission.", exception);
            }
        }

        if (resolvedChannel is not IMessageChannel channel)
        {
            throw new InvalidOperationException(
                $"Discord channel {_options.AnnouncementChannelId} is not an accessible message channel. " +
                "Set Discord__AnnouncementChannelId to a text or announcement channel ID.");
        }

        await channel.SendMessageAsync(embed: embed, options: requestOptions);
    }

    private static string FormatScheduledMatch(CounterStrikeMatch match)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:t>"
            : "TBD";
        return $"{start} — **{GetMatchup(match)}** · {match.LeagueName}";
    }

    private static string FormatMatchDetails(CounterStrikeMatch match)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:F>"
            : "Start time TBD";
        return $"**{GetMatchup(match)}**\n{match.LeagueName} · {match.TournamentName}\n{start}";
    }

    private static string GetMatchup(CounterStrikeMatch match) => match.Opponents.Count >= 2
        ? $"{match.Opponents.ElementAt(0).Name} vs {match.Opponents.ElementAt(1).Name}"
        : match.Name;

    private static string FormatScore(CounterStrikeMatch match)
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

    private static string? GetTeamName(CounterStrikeMatch match, long? teamId) =>
        match.Opponents.FirstOrDefault(opponent => opponent.Id == teamId)?.Name;
}
