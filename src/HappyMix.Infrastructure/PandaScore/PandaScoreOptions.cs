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
    /// <summary>CS2 main teams followed when neither IDs nor aliases are configured.</summary>
    public static readonly long[] BuiltInCounterStrikeTeamIds = [3216, 136955, 131441, 129381, 136497, 3210];
    /// <summary>Dota 2 main teams followed when neither IDs nor aliases are configured.</summary>
    public static readonly long[] BuiltInDota2TeamIds = [1699, 132557];

    /// <summary>The CS2 teams a server without its own list follows.</summary>
    public long[] DefaultCounterStrikeTeams => CounterStrikeTeamIds ??
        (CounterStrikeTeamAliases.Count > 0 ? CounterStrikeTeamAliases.Values.Distinct().ToArray() : BuiltInCounterStrikeTeamIds);
    /// <summary>The Dota 2 teams a server without its own list follows.</summary>
    public long[] DefaultDota2Teams => Dota2TeamIds ??
        (Dota2TeamAliases.Count > 0 ? Dota2TeamAliases.Values.Distinct().ToArray() : BuiltInDota2TeamIds);

    /// <summary>Names accepted by the CS2 team settings command.</summary>
    public Dictionary<string, long> CounterStrikeTeamAliases { get; init; } = [];
    /// <summary>Names accepted by the Dota 2 team settings command.</summary>
    public Dictionary<string, long> Dota2TeamAliases { get; init; } = [];
}
