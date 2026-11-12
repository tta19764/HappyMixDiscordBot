namespace HappyMix.Bot.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string Token { get; init; } = string.Empty;

    public ulong AnnouncementChannelId { get; init; }

    public string[] CounterStrikeTwitchChannels { get; init; } = [];

    public string[] Dota2TwitchChannels { get; init; } = [];
}
