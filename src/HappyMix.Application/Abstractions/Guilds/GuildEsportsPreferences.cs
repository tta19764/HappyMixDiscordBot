using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Guilds;

[Flags]
public enum EsportsNotifications
{
    None = 0,
    Schedule = 1,
    Reminders = 2,
    Maps = 4,
    Results = 8,
    All = Schedule | Reminders | Maps | Results
}

/// <summary>
/// Saved esports settings of a server. A null language or discovery value means "never configured": the deployment's
/// <see cref="CommunityDefaults"/> apply, so a new server gets Ukrainian notifications and Ukrainian broadcast
/// preferences without any setup, while an explicit choice is never replaced.
/// </summary>
public sealed record GuildEsportsPreferences(
    long[]? CounterStrikeTeamIds = null,
    long[]? Dota2TeamIds = null,
    EsportsNotifications Notifications = EsportsNotifications.All,
    int ReminderMinutes = 30,
    ulong[]? OrganizerRoleIds = null,
    ulong? CounterStrikePingRoleId = null,
    ulong? Dota2PingRoleId = null,
    Dictionary<string, long>? CounterStrikeTeamAliases = null,
    Dictionary<string, long>? Dota2TeamAliases = null,
    string[]? CounterStrikeTwitchChannels = null,
    string[]? Dota2TwitchChannels = null,
    ulong? CounterStrikeChannelId = null,
    ulong? Dota2ChannelId = null,
    bool MatchStarts = true,
    string? CounterStrikeLanguage = null,
    string? Dota2Language = null,
    StreamDiscoveryPreferences? CounterStrikeDiscovery = null,
    StreamDiscoveryPreferences? Dota2Discovery = null)
{
    public static GuildEsportsPreferences Default { get; } = new();

    public ulong? ChannelFor(Esport game) => game == Esport.CounterStrike2 ? CounterStrikeChannelId : Dota2ChannelId;

    /// <summary>The notification language of <paramref name="game"/>: the saved choice, else the community default.</summary>
    public string LanguageFor(Esport game, CommunityDefaults defaults) =>
        CommunityDefaults.Normalize(game == Esport.CounterStrike2 ? CounterStrikeLanguage : Dota2Language) ?? defaults.LanguageCode;

    /// <summary>
    /// Effective discovery settings: the saved ones, with the community lists filled in when the server follows them,
    /// or the community preferences (switched off) when nothing was saved.
    /// </summary>
    public StreamDiscoveryPreferences DiscoveryFor(Esport game, CommunityDefaults defaults) =>
        (game == Esport.CounterStrike2 ? CounterStrikeDiscovery : Dota2Discovery) switch
        {
            null => defaults.Discovery(),
            { CommunityLists: true } saved => defaults.Discovery(saved.Enabled) with { Priority = saved.Priority, CommunityLists = true },
            var saved => saved
        };

    public GuildEsportsPreferences WithLanguage(Esport game, string? language) => game == Esport.CounterStrike2
        ? this with { CounterStrikeLanguage = language }
        : this with { Dota2Language = language };

    public GuildEsportsPreferences WithDiscovery(Esport game, StreamDiscoveryPreferences? discovery) => game == Esport.CounterStrike2
        ? this with { CounterStrikeDiscovery = discovery }
        : this with { Dota2Discovery = discovery };

    /// <summary>
    /// Which games post to which channel. Games without their own channel use <paramref name="serverChannel"/>;
    /// games with no channel at all are left out.
    /// </summary>
    public IReadOnlyList<AnnouncementRoute> Routes(ulong serverChannel) =>
        new[] { Esport.CounterStrike2, Esport.Dota2 }
            .Select(game => (Game: game, Channel: ChannelFor(game) ?? serverChannel))
            .Where(route => route.Channel != 0)
            .GroupBy(route => route.Channel, route => route.Game)
            .Select(group => new AnnouncementRoute(group.Key, group.ToArray()))
            .ToArray();
}

/// <summary>A channel and the games whose match posts go there.</summary>
public sealed record AnnouncementRoute(ulong ChannelId, IReadOnlyList<Esport> Games);

/// <summary>
/// Optional Twitch discovery settings. Lists are comma-separated and ordered by preference. <see cref="Priority"/>
/// lists the criteria that take part in ranking, highest first.
/// </summary>
/// <param name="CommunityLists">
/// When true the language, country and tag lists come from <see cref="CommunityDefaults"/> and follow later changes to them.
/// </param>
public sealed record StreamDiscoveryPreferences(bool Enabled = false, string Languages = "", string Countries = "",
    string Tags = "", string Priority = StreamDiscoveryPreferences.DefaultPriority, bool CommunityLists = false)
{
    public const string DefaultPriority = "language,country,tags,viewers";
    public static readonly string[] Criteria = ["language", "country", "tags", "viewers"];
    public const int MaxItems = 10;

    public static string[] Split(string? value) =>
        (value ?? "").Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Cleans a typed list: trims, drops a leading '#', removes duplicates (ignoring case) and keeps the order.
    /// Language codes are lower-cased because Twitch reports them that way.
    /// </summary>
    public static string NormalizeList(string? value, bool languageCodes = false) => string.Join(",",
        Split(value).Select(item => item.TrimStart('#').Trim()).Where(item => item.Length > 0)
            .Select(item => languageCodes ? item.ToLowerInvariant() : item)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Typed in a list option to empty that list.</summary>
    public const string Clear = "-";
    /// <summary>Typed in a list or priority option to go back to the community preferences.</summary>
    public const string UseDefault = "default";

    /// <summary>
    /// Applies optional command values to these (effective) settings. Null keeps a value, <see cref="Clear"/> empties a
    /// list, and <see cref="UseDefault"/> in any list follows the community lists again (or restores the default
    /// priority). Typing any other list value switches to custom lists, keeping the current values of the other lists.
    /// </summary>
    public StreamDiscoveryPreferences Merge(bool? enabled, string? languages, string? countries, string? tags, string? priority)
    {
        static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        static string Pick(string? value, string current) => value is null ? current : Is(value, Clear) ? "" : value;
        string[] lists = [languages ?? "", countries ?? "", tags ?? ""];
        bool community = lists.Any(value => Is(value, UseDefault));
        bool typed = !community && new[] { languages, countries, tags }.Any(value => value is not null);
        var merged = this with
        {
            Enabled = enabled ?? Enabled,
            Priority = priority is null ? Priority : Is(priority, UseDefault) ? DefaultPriority : priority,
            CommunityLists = community || (CommunityLists && !typed)
        };
        if (typed)
            merged = merged with { Languages = Pick(languages, Languages), Countries = Pick(countries, Countries), Tags = Pick(tags, Tags) };
        return merged.Normalize();
    }

    public StreamDiscoveryPreferences Normalize() => new(Enabled, NormalizeList(Languages, true), NormalizeList(Countries),
        NormalizeList(Tags), NormalizeList(Priority, true), CommunityLists);

    public bool IsValid => Split(Priority).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Split(Priority).Length
        && Split(Priority).All(value => Criteria.Contains(value, StringComparer.OrdinalIgnoreCase))
        && new[] { Languages, Countries, Tags }.All(list => list.Length <= 200 && Split(list).Length <= MaxItems
            && Split(list).All(item => item.TrimStart('#').Length is > 0 and <= 25))
        && Split(Languages).All(code => code.Length is >= 2 and <= 8 && code.All(character => char.IsAsciiLetter(character) || character == '-'))
        && Priority.Length <= 100;
}

/// <summary>
/// Deployment-wide starting values for servers that haven't chosen their own. The built-in values target a Ukrainian
/// community: Ukrainian notifications, and Twitch discovery that prefers Ukrainian-language streams and the tags
/// Ukrainian broadcasters commonly use. Bound from the <c>CommunityDefaults</c> configuration section.
/// </summary>
public sealed class CommunityDefaults
{
    public static CommunityDefaults Ukrainian { get; } = new();

    /// <summary>Notification language for match posts and group/event reminders: en, uk or ru.</summary>
    public string Language { get; set; } = "uk";
    /// <summary>Preferred Twitch stream languages, highest first.</summary>
    public string StreamLanguages { get; set; } = "uk";
    /// <summary>Broadcaster tags naming a country. Self-declared by streamers, not a verified location.</summary>
    public string StreamCountries { get; set; } = "Ukraine,UA,Україна";
    /// <summary>Other preferred broadcaster tags.</summary>
    public string StreamTags { get; set; } = "Ukrainian,Українська,УкраїнськоюМовою";

    public string LanguageCode => Normalize(Language) ?? "en";

    public StreamDiscoveryPreferences Discovery(bool enabled = false) =>
        new StreamDiscoveryPreferences(enabled, StreamLanguages, StreamCountries, StreamTags, CommunityLists: true).Normalize();

    /// <summary>en, uk or ru; anything else is null.</summary>
    public static string? Normalize(string? language) => language?.Trim().ToLowerInvariant() is "en" or "uk" or "ru"
        ? language.Trim().ToLowerInvariant() : null;
}
