using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Stats;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Posts the Dota 2 meta in the community language: a header, then one block per lane or role with each hero's icon,
/// numbers and build links (STRATZ guides and Dota2ProTracker). Discord allows 10 embeds and 6000 characters per message,
/// so a long post continues in a second or third message. Returns why it couldn't post.
/// </summary>
internal sealed partial class DiscordDotaMetaPublisher(DiscordSocketClient discord, IOptions<CommunityDefaults> community, HeroEmojis emojis,
    ILogger<DiscordDotaMetaPublisher> logger) : IDotaMetaPublisher
{
    /// <summary>Room left per message under Discord's 6000-character limit for all its embeds.</summary>
    internal const int MessageBudget = 5800;
    private static readonly Color Accent = new(0xF2906F);
    /// <summary>One colour per lane or position, in order, so the blocks are easy to tell apart.</summary>
    private static readonly Color[] SectionColors =
        [new(0xE8B04B), new(0x6FA8F2), new(0xE0605E), new(0x7BC47F), new(0xB58BE8), new(0x9AA0A6)];

    public async Task<string?> PublishAsync(ulong guildId, ulong channelId, DotaMeta meta, int count, CancellationToken cancellationToken)
    {
        var heroes = meta.Lanes.Values.SelectMany(lane => lane.Take(count)).Select(hero => (hero.HeroId, hero.Image));
        var icons = await emojis.GetAsync(heroes, cancellationToken);
        return await SendAsync(guildId, channelId, Messages(meta, count, community.Value.LanguageCode, icons), cancellationToken);
    }

    public async Task<string?> PublishRolesAsync(ulong guildId, ulong channelId, IReadOnlyList<HeroMeta> roles, int count, CancellationToken cancellationToken)
    {
        var heroes = roles.SelectMany(role => role.Heroes.Take(count)).Select(hero => (hero.HeroId, hero.Image));
        var icons = await emojis.GetAsync(heroes, cancellationToken);
        return await SendAsync(guildId, channelId, RoleMessages(roles, count, community.Value.LanguageCode, icons), cancellationToken);
    }

    private async Task<string?> SendAsync(ulong guildId, ulong channelId, IReadOnlyList<Embed[]> messages, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return "The bot isn't connected to Discord right now.";
        if (discord.GetGuild(guildId)?.GetTextChannel(channelId) is not { } channel)
            return "The meta channel no longer exists. Choose or create another one.";
        try
        {
            foreach (var embeds in messages)
                await channel.SendMessageAsync(embeds: embeds, allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { CancelToken = cancellationToken });
            return null;
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotPosted(logger, guildId, channelId, error);
            return "Discord refused the message: " + (error.DiscordCode?.ToString() ?? error.HttpCode.ToString())
                + ". Check that HappyMix can see the channel and send messages there.";
        }
    }

    /// <summary>"Dota 2 meta by lane" from OpenDota: one block per lane with its <paramref name="count"/> strongest heroes.</summary>
    internal static IReadOnlyList<Embed[]> Messages(DotaMeta meta, int count, string language, IReadOnlyDictionary<int, string>? icons = null)
    {
        var culture = Culture(language);
        var header = new EmbedBuilder()
            .WithTitle(NotificationText.Get(language, "Meta_Title"))
            .WithDescription(NotificationText.Get(language, "Meta_PostLead") + "\n" + NotificationText.Get(language, "Meta_BuildsLegend"))
            .WithColor(Accent)
            .WithUrl("https://www.opendota.com/heroes");
        var sections = meta.Lanes.OrderBy(pair => pair.Key).Select(pair => (
            Title: NotificationText.Get(language, $"Meta_Lane_{pair.Key}"),
            Top: pair.Value.Count > 0 ? pair.Value[0].Image : null,
            Lines: pair.Value.Take(count).Select((hero, index) =>
                Line(index, hero.HeroId, hero.Name, icons,
                    $"{hero.WinRate.ToString("0.0", culture)}% · {hero.Games.ToString("N0", culture)}")).ToList()))
            .ToList();
        return Assemble(header, sections, meta.FetchedAt, "OpenDota");
    }

    /// <summary>"Dota 2 meta by role" from STRATZ with the server's filters: one block per position.</summary>
    internal static IReadOnlyList<Embed[]> RoleMessages(IReadOnlyList<HeroMeta> roles, int count, string language,
        IReadOnlyDictionary<int, string>? icons = null)
    {
        var culture = Culture(language);
        var first = roles[0];
        string scope = (first.Filter.Patch == MetaPatch.Current && first.PatchName is { } patch
                ? NotificationText.Get(language, "Meta_Patch", patch) : NotificationText.Get(language, "Meta_Patch_LastWeek"))
            + " · " + NotificationText.Get(language, $"Meta_Rank_{first.Filter.Rank}")
            + " · " + NotificationText.Get(language, $"Meta_Sort_{first.Filter.Sort}");
        var header = new EmbedBuilder()
            .WithTitle(NotificationText.Get(language, "Meta_RolesTitle"))
            .WithDescription(scope + "\n" + NotificationText.Get(language, "Meta_RolesLegend") + "\n" + NotificationText.Get(language, "Meta_BuildsLegend"))
            .WithColor(Accent)
            .WithUrl("https://stratz.com/heroes");
        var sections = roles.Select(role => (
            Title: NotificationText.Get(language, $"Meta_Position_{role.Filter.Position}"),
            Top: role.Heroes.Count > 0 ? role.Heroes[0].Image : null,
            Lines: role.Heroes.Take(count).Select((hero, index) =>
                Line(index, hero.HeroId, hero.Name, icons,
                    $"{hero.WinRate.ToString("0.0", culture)}% · {hero.PickRate.ToString("0.0", culture)}%")).ToList()))
            .ToList();
        return Assemble(header, sections, first.FetchedAt, "STRATZ");
    }

    /// <summary>"3. 🟣 **Lina** 54.1% · 12,345 · [STRATZ](…) · [D2PT](…)", with the hero's icon when the bot has it.</summary>
    internal static string Line(int index, int heroId, string name, IReadOnlyDictionary<int, string>? icons, string numbers) =>
        $"{index + 1}. {(icons?.GetValueOrDefault(heroId) is { } icon ? icon + " " : "")}**{Format.Sanitize(name)}** {numbers}"
        + $" · [STRATZ]({DotaBuilds.Stratz(heroId)}) · [D2PT]({DotaBuilds.ProTracker(name)})";

    /// <summary>
    /// The header and one embed per section, packed into as few messages as Discord allows. A section that alone is
    /// too long for its embed loses its last heroes; the footer and time go on the last embed.
    /// </summary>
    internal static IReadOnlyList<Embed[]> Assemble(EmbedBuilder header, IReadOnlyList<(string Title, string? Top, List<string> Lines)> sections,
        DateTimeOffset fetchedAt, string source)
    {
        var builders = new List<EmbedBuilder> { header };
        for (int index = 0; index < sections.Count; index++)
        {
            var (title, top, lines) = sections[index];
            while (lines.Count > 1 && lines.Sum(line => line.Length + 1) > Math.Min(EmbedBuilder.MaxDescriptionLength, MessageBudget - 300))
                lines.RemoveAt(lines.Count - 1);
            var section = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(lines.Count == 0 ? "—" : string.Join('\n', lines))
                .WithColor(SectionColors[index % SectionColors.Length]);
            if (top is { Length: > 0 } && Uri.TryCreate(top, UriKind.Absolute, out _))
                section.WithThumbnailUrl(top);
            builders.Add(section);
        }
        builders[^1].WithFooter(source).WithTimestamp(fetchedAt);

        var messages = new List<Embed[]>();
        var current = new List<Embed>();
        int length = 0;
        foreach (var embed in builders.Select(builder => builder.Build()))
        {
            if (current.Count > 0 && (current.Count == DiscordConfig.MaxEmbedsPerMessage || length + embed.Length > MessageBudget))
            {
                messages.Add([.. current]);
                current.Clear();
                length = 0;
            }
            current.Add(embed);
            length += embed.Length;
        }
        if (current.Count > 0)
            messages.Add([.. current]);
        return messages;
    }

    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language is "uk" or "ru" ? language : "en");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dota 2 meta for server {GuildId} was not posted in channel {ChannelId}")]
    private static partial void LogNotPosted(ILogger logger, ulong guildId, ulong channelId, Exception error);
}
