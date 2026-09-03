namespace HappyMix.Bot.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public string Token { get; init; } = string.Empty;
}
