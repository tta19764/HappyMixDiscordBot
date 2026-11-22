using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Stats;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>Posts the Dota 2 lane meta as one embed, in the community language. Returns why it couldn't post.</summary>
internal sealed partial class DiscordDotaMetaPublisher(DiscordSocketClient discord, IOptions<CommunityDefaults> community,
    ILogger<DiscordDotaMetaPublisher> logger) : IDotaMetaPublisher
{
    public Task<string?> PublishAsync(ulong guildId, ulong channelId, DotaMeta meta, int count, CancellationToken cancellationToken) =>
        SendAsync(guildId, channelId, Embed(meta, count, community.Value.LanguageCode), cancellationToken);

    public Task<string?> PublishRolesAsync(ulong guildId, ulong channelId, IReadOnlyList<HeroMeta> roles, int count, CancellationToken cancellationToken) =>
        SendAsync(guildId, channelId, RolesEmbed(roles, count, community.Value.LanguageCode), cancellationToken);

    private async Task<string?> SendAsync(ulong guildId, ulong channelId, Embed embed, CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return "The bot isn't connected to Discord right now.";
        if (discord.GetGuild(guildId)?.GetTextChannel(channelId) is not { } channel)
            return "The meta channel no longer exists. Choose or create another one.";
        try
        {
            await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
            return null;
        }
        catch (Discord.Net.HttpException error)
        {
            LogNotPosted(logger, guildId, channelId, error);
            return "Discord refused the message: " + (error.DiscordCode?.ToString() ?? error.HttpCode.ToString())
                + ". Check that HappyMix can see the channel and send messages there.";
        }
    }

    /// <summary>
    /// "Dota 2 meta by lane": one field per lane with its <paramref name="count"/> strongest heroes, win rate and games.
    /// Lines are kept short so 20 heroes still fit Discord's 1024-character field limit.
    /// </summary>
    internal static Embed Embed(DotaMeta meta, int count, string language)
    {
        var culture = CultureInfo.GetCultureInfo(language is "uk" or "ru" ? language : "en");
        var embed = new EmbedBuilder()
            .WithTitle(NotificationText.Get(language, "Meta_Title"))
            .WithDescription(NotificationText.Get(language, "Meta_PostLead"))
            .WithColor(new Color(0xF2906F))
            .WithUrl("https://www.opendota.com/heroes")
            .WithTimestamp(meta.FetchedAt)
            .WithFooter("OpenDota");
        foreach (var (lane, heroes) in meta.Lanes.OrderBy(pair => pair.Key))
            embed.AddField(NotificationText.Get(language, $"Meta_Lane_{lane}"),
                heroes.Count == 0 ? "—" : Field(heroes.Take(count).Select((hero, index) =>
                    $"{index + 1}. **{Format.Sanitize(hero.Name)}** {hero.WinRate.ToString("0.0", culture)}% · {hero.Games.ToString("N0", culture)}")),
                inline: true);
        return embed.Build();
    }

    /// <summary>
    /// "Dota 2 meta by role": one field per position with its strongest heroes by the server's filters, from STRATZ.
    /// Discord allows 6000 characters per embed, so with 5 roles of 20 heroes the lists are shortened evenly to fit.
    /// </summary>
    internal static Embed RolesEmbed(IReadOnlyList<HeroMeta> roles, int count, string language)
    {
        var culture = CultureInfo.GetCultureInfo(language is "uk" or "ru" ? language : "en");
        var first = roles[0];
        string scope = (first.Filter.Patch == MetaPatch.Current && first.PatchName is { } patch
                ? NotificationText.Get(language, "Meta_Patch", patch) : NotificationText.Get(language, "Meta_Patch_LastWeek"))
            + " · " + NotificationText.Get(language, $"Meta_Rank_{first.Filter.Rank}")
            + " · " + NotificationText.Get(language, $"Meta_Sort_{first.Filter.Sort}");
        var columns = roles.Select(role => role.Heroes.Take(count).Select((hero, index) =>
            $"{index + 1}. **{Format.Sanitize(hero.Name)}** {hero.WinRate.ToString("0.0", culture)}% · {hero.PickRate.ToString("0.0", culture)}%").ToList()).ToList();
        while (columns.Sum(lines => lines.Sum(line => line.Length + 1)) > 5000 && columns.Any(lines => lines.Count > 3))
            foreach (var lines in columns.Where(lines => lines.Count > 3))
                lines.RemoveAt(lines.Count - 1);
        var embed = new EmbedBuilder()
            .WithTitle(NotificationText.Get(language, "Meta_RolesTitle"))
            .WithDescription(scope + "\n" + NotificationText.Get(language, "Meta_RolesLegend"))
            .WithColor(new Color(0xF2906F))
            .WithUrl("https://stratz.com/heroes")
            .WithTimestamp(first.FetchedAt)
            .WithFooter("STRATZ");
        for (int index = 0; index < roles.Count; index++)
            embed.AddField(NotificationText.Get(language, $"Meta_Position_{roles[index].Filter.Position}"),
                columns[index].Count == 0 ? "—" : Field(columns[index]), inline: true);
        return embed.Build();
    }

    /// <summary>Lines joined until Discord's field limit; anything past it is left out.</summary>
    private static string Field(IEnumerable<string> lines)
    {
        var text = new System.Text.StringBuilder();
        foreach (string line in lines)
        {
            if (text.Length + line.Length + 1 > EmbedFieldBuilder.MaxFieldValueLength)
                break;
            text.Append(text.Length == 0 ? "" : "\n").Append(line);
        }
        return text.ToString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dota 2 meta for server {GuildId} was not posted in channel {ChannelId}")]
    private static partial void LogNotPosted(ILogger logger, ulong guildId, ulong channelId, Exception error);
}
