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
    /// <summary>Exact CS2 IDs. Null uses NAVI/FUT/Monte defaults; empty disables CS2.</summary>
    /// <remarks>No initialized array: configuration binding must replace rather than append defaults.</remarks>
    public long[]? CounterStrikeTeamIds { get; init; }
    /// <summary>Exact Dota IDs. Null uses NAVI/Monte defaults; empty disables Dota. No FUT identity found on 2026-11-09.</summary>
    public long[]? Dota2TeamIds { get; init; }
}
