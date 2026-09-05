namespace HappyMix.Infrastructure.PandaScore;

public sealed class PandaScoreOptions
{
    public const string SectionName = "PandaScore";

    public string BaseUrl { get; init; } = "https://api.pandascore.co/";

    public string Token { get; init; } = string.Empty;

    public string TimeZoneId { get; init; } = "Europe/Kyiv";

    public string[] IncludedTournamentTiers { get; init; } = ["s", "a"];
}
