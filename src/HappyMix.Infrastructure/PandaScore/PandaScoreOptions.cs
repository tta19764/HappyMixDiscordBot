namespace HappyMix.Infrastructure.PandaScore;

/// <summary>Configures provider access and exact main-roster selection per discipline.</summary>
public sealed class PandaScoreOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "PandaScore";
    /// <summary>Provider REST API root.</summary>
    public string BaseUrl { get; init; } = "https://api.pandascore.co/";
    /// <summary>Private bearer token; never log this value.</summary>
    public string Token { get; init; } = string.Empty;
    /// <summary>Timezone used for daily schedule boundaries.</summary>
    public string TimeZoneId { get; init; } = "Europe/Kyiv";
    /// <summary>Optional tier restriction. Empty includes all tiers for followed teams.</summary>
    public string[] IncludedTournamentTiers { get; init; } = [];
    /// <summary>Optional CS2 default override. Null uses configured aliases or built-in defaults. Empty disables CS2.</summary>
    /// <remarks>No initialized array: configuration binding must replace rather than append defaults.</remarks>
    public long[]? CounterStrikeTeamIds { get; init; }
    /// <summary>Optional Dota 2 default override. Null uses configured aliases or built-in defaults. Empty disables Dota 2.</summary>
    public long[]? Dota2TeamIds { get; init; }
    /// <summary>Names accepted by the CS2 team settings command.</summary>
    public Dictionary<string, long> CounterStrikeTeamAliases { get; init; } = [];
    /// <summary>Names accepted by the Dota 2 team settings command.</summary>
    public Dictionary<string, long> Dota2TeamAliases { get; init; } = [];
}
