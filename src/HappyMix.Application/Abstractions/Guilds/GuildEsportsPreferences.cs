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
    string CounterStrikeLanguage = "en",
    string Dota2Language = "en",
    StreamDiscoveryPreferences? CounterStrikeDiscovery = null,
    StreamDiscoveryPreferences? Dota2Discovery = null)
{
    public static GuildEsportsPreferences Default { get; } = new();
}

public sealed record StreamDiscoveryPreferences(bool Enabled = false, string Languages = "", string Countries = "",
    string Tags = "", string Priority = "language,country,tags,viewers")
{
    public static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public bool IsValid => Split(Priority).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Split(Priority).Length
        && Split(Priority).All(value => value is "language" or "country" or "tags" or "viewers")
        && Languages.Length <= 200 && Countries.Length <= 200 && Tags.Length <= 200 && Priority.Length <= 100;
}
