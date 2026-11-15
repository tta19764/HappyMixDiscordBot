namespace HappyMix.Bot.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public int MaxFollowedTeamsPerGame { get; init; } = 20;

    public int MaxTwitchChannelsPerGame { get; init; } = 5;

    public string Token { get; init; } = string.Empty;

    public ulong AnnouncementChannelId { get; init; }

    public string[] CounterStrikeTwitchChannels { get; init; } = [];

    public string[] Dota2TwitchChannels { get; init; } = [];
}
