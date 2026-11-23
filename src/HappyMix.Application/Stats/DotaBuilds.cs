namespace HappyMix.Application.Stats;

/// <summary>Where to read a Dota 2 hero's current builds: STRATZ guides and Dota2ProTracker (high-MMR and pro players).</summary>
public static class DotaBuilds
{
    /// <summary>STRATZ's guides for the hero: items, skills and talents from high-rank games.</summary>
    public static string Stratz(int heroId) =>
        "https://stratz.com/heroes/" + heroId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/guides";

    /// <summary>Dota2ProTracker's page for the hero, which uses the English hero name ("Nature's Prophet").</summary>
    public static string ProTracker(string heroName) =>
        "https://dota2protracker.com/hero/" + Uri.EscapeDataString(heroName.Trim()).Replace("'", "%27", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
}
