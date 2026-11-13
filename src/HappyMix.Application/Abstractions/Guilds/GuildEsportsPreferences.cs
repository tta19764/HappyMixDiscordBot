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
    bool MatchStarts = true)
{
    public static GuildEsportsPreferences Default { get; } = new();
}
