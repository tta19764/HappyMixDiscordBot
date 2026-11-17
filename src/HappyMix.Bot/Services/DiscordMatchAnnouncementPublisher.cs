using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Application.Matches.SynchronizeMatches;
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
    TwitchLiveStreams twitch,
    IOptions<CommunityDefaults> community,
    GuildSettings? settings = null) : IMatchAnnouncementPublisher, IGuildAnnouncementPublisher
{
    private readonly DiscordOptions _options = options.Value;
    private GuildEsportsPreferences Preferences => settings?.Preferences ?? GuildEsportsPreferences.Default;
    /// <summary>Each game posts in its own language, even when several games share one channel.</summary>
    private string Language(Esport game) => Preferences.LanguageFor(game, community.Value);
    private string Text(Esport game, string key, params object[] values) => NotificationText.Get(Language(game), key, values);
    private ulong ChannelId => settings?.ChannelId ?? _options.AnnouncementChannelId;

    /// <summary>The games whose posts go to this publisher's channel.</summary>
    private IReadOnlyList<Esport> ChannelGames => settings is null
        ? [Esport.CounterStrike2, Esport.Dota2]
        : Preferences.Routes(settings.ChannelId).FirstOrDefault(route => route.ChannelId == settings.ChannelId)?.Games
          ?? [Esport.CounterStrike2, Esport.Dota2];

    internal DiscordMatchAnnouncementPublisher ForGuild(GuildSettings destination) =>
        new(client, options, twitch, community, destination);

    IMatchAnnouncementPublisher IGuildAnnouncementPublisher.ForGuild(GuildSettings destination) => ForGuild(destination);

    /// <inheritdoc />
    public async Task PublishDailyScheduleAsync(
        DateOnly matchDate,
        IReadOnlyCollection<EsportsMatch> matches,
        CancellationToken cancellationToken)
    {
        // An empty day gets one note per language used in this channel, naming only the games that post here.
        var embeds = matches.Count == 0
            ? ChannelGames.GroupBy(Language).SelectMany(group =>
                BuildDailyScheduleEmbeds(matchDate, matches, FormatTwitchLinks, group.Key, group.ToArray()))
            : matches.GroupBy(match => match.Game).SelectMany(group =>
                BuildDailyScheduleEmbeds(matchDate, group.ToArray(), FormatTwitchLinks, Language(group.Key)));
        foreach (Embed embed in embeds)
        {
            await SendAsync(embed, cancellationToken);
        }
    }

    /// <summary>Builds bounded pages without merging tournaments across disciplines.</summary>
    internal static IReadOnlyList<Embed> BuildDailyScheduleEmbeds(
        DateOnly matchDate,
        IReadOnlyCollection<EsportsMatch> matches,
        Func<EsportsMatch, string, string>? formatTwitchLinks = null, string language = "en",
        IReadOnlyCollection<Esport>? games = null)
    {
        if (matches.Count == 0)
        {
            games = games is { Count: > 0 } ? games : [Esport.CounterStrike2, Esport.Dota2];
            string names = string.Join(" / ", games.Select(ShortName));
            string empty = games.Count > 1
                ? NotificationText.Get(language, "Notify_NoMatches")
                : NotificationText.Get(language, "Notify_NoMatchesGame", names);
            return BuildTournamentScheduleEmbeds(matchDate, [], NotificationText.Get(language, "Notify_ScheduleTitle", names),
                formatTwitchLinks, language, empty);
        }

        return matches.OrderBy(match => match.BeginAt)
            .GroupBy(match => (match.Game, match.TournamentId,
                Fallback: match.TournamentId is null ? $"{match.LeagueName} • {match.TournamentName}" : string.Empty))
            .SelectMany(group => BuildTournamentScheduleEmbeds(matchDate, group.ToArray(),
                $"{group.First().Game.DisplayName()} • {group.First().LeagueName} • {group.First().TournamentName}",
                formatTwitchLinks, language))
            .ToArray();
    }

    private static Embed[] BuildTournamentScheduleEmbeds(
        DateOnly matchDate,
        EsportsMatch[] matches,
        string tournamentTitle,
        Func<EsportsMatch, string, string>? formatTwitchLinks = null, string language = "en", string? empty = null)
    {
        const int descriptionLimit = 4096;
        IEnumerable<string> lines = matches.Length == 0
            ? [empty ?? NotificationText.Get(language, "Notify_NoMatches")]
            : matches.Select(match => FormatScheduledMatch(match, language) + formatTwitchLinks?.Invoke(match, language));
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
            .WithFooter(NotificationText.Get(language, "Notify_ScheduleFooter", matchDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), index + 1, descriptions.Count))
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .Build()).ToArray();
    }

    /// <inheritdoc />
    public async Task PublishStartingSoonAsync(
        EsportsMatch match,
        CancellationToken cancellationToken)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • {Text(match.Game, "Notify_Soon", Preferences.ReminderMinutes)}")
            .WithDescription(FormatMatchDetails(match, Language(match.Game)) + await FormatLiveTwitchLinksAsync(match, cancellationToken))
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();

        ulong? role = match.Game == Esport.CounterStrike2
            ? settings?.Preferences?.CounterStrikePingRoleId : settings?.Preferences?.Dota2PingRoleId;
        await SendAsync(embed, cancellationToken, role);
    }

    /// <inheritdoc />
    public async Task PublishMatchStartedAsync(EsportsMatch match, CancellationToken cancellationToken)
    {
        string streams = await FormatLiveTwitchLinksAsync(match, cancellationToken);
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • {Text(match.Game, "Notify_Started")}")
            .WithDescription(FormatMatchDetails(match, Language(match.Game)) + streams)
            .WithColor(Color.Green)
            .WithCurrentTimestamp()
            .Build();
        await SendAsync(embed, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishMapResultAsync(
        EsportsMatch match,
        EsportsGame game,
        CancellationToken cancellationToken)
    {
        var embed = BuildGameResultEmbed(match, game, Language(match.Game)).ToEmbedBuilder();
        embed.WithDescription(embed.Description + await FormatLiveTwitchLinksAsync(match, cancellationToken));
        await SendAsync(embed.Build(), cancellationToken);
    }

    /// <summary>Formats an individual result using map terminology for CS2 and game terminology for Dota 2.</summary>
    /// <param name="match">The parent series and discipline.</param>
    /// <param name="game">The completed map/game.</param>
    /// <returns>The result embed without sending it to Discord.</returns>
    internal static Embed BuildGameResultEmbed(EsportsMatch match, EsportsGame game, string language = "en")
    {
        string winner = GetTeamName(match, game.WinnerId) ?? NotificationText.Get(language, "Notify_UnknownWinner");
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • {NotificationText.Get(language, match.Game == Esport.CounterStrike2 ? "Notify_MapFinished" : "Notify_GameFinished", game.Position)}")
            .WithDescription(NotificationText.Get(language, match.Game == Esport.CounterStrike2 ? "Notify_MapWinner" : "Notify_GameWinner", winner) + "\n" + FormatScore(match))
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
        string winner = GetTeamName(match, match.WinnerId) ?? Text(match.Game, "Notify_UnknownWinner");
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Game.DisplayName()} • {Text(match.Game, "Notify_Finished")}")
            .WithDescription(Text(match.Game, "Notify_Winner", winner) + "\n" + FormatScore(match))
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

    /// <summary>
    /// Live broadcast lines: the server's selected channels first, then (when discovery is on) other live streams
    /// whose titles name both teams, ranked by the server's preferences. Each line is in the game's language.
    /// </summary>
    private async Task<string> FormatLiveTwitchLinksAsync(EsportsMatch match, CancellationToken cancellationToken)
    {
        var preferred = GetTwitchChannels(match).Select(TwitchChannels.GetHandle).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        // A channel in the right category may be showing another match; only link streams whose title names both teams.
        var live = await twitch.GetLiveAsync(preferred, match.Game, cancellationToken, match.Opponents);
        var discovery = Preferences.DiscoveryFor(match.Game, community.Value);
        var discovered = discovery.Enabled
            ? (await twitch.DiscoverAsync(match.Game, match.Opponents, discovery, cancellationToken))
                .Where(handle => !live.Contains(handle, StringComparer.OrdinalIgnoreCase)).ToArray()
            : [];
        return StreamLines(Language(match.Game), live, discovered);
    }

    internal static string StreamLines(string language, IReadOnlyCollection<string> selected, IReadOnlyCollection<string> discovered)
    {
        static string Links(IEnumerable<string> handles) => string.Join(" · ", handles.Select(handle => $"[{handle}](https://www.twitch.tv/{handle})"));
        string text = selected.Count == 0 ? "" : $"\n{NotificationText.Get(language, "Notify_SelectedStreams")}: {Links(selected)}";
        return discovered.Count == 0 ? text : $"{text}\n{NotificationText.Get(language, "Notify_DiscoveredStreams")}: {Links(discovered)}";
    }

    private string FormatTwitchLinks(EsportsMatch match, string language) => TwitchChannels.Format(match, GetTwitchChannels(match), language);

    private static string ShortName(Esport game) => game == Esport.CounterStrike2 ? "CS2" : "Dota 2";

    private string[] GetTwitchChannels(EsportsMatch match) => match.Game == Esport.CounterStrike2
            ? settings?.Preferences?.CounterStrikeTwitchChannels ?? _options.CounterStrikeTwitchChannels
            : settings?.Preferences?.Dota2TwitchChannels ?? _options.Dota2TwitchChannels;

    private static string FormatScheduledMatch(EsportsMatch match, string language)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:t>"
            : language == "en" ? "TBD" : NotificationText.Get(language, "Notify_Tbd");
        return $"{start} — **{GetMatchup(match)}** · {match.LeagueName}";
    }

    private static string FormatMatchDetails(EsportsMatch match, string language)
    {
        string start = match.BeginAt is { } beginAt
            ? $"<t:{beginAt.ToUnixTimeSeconds()}:F>"
            : NotificationText.Get(language, "Notify_Tbd");
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
