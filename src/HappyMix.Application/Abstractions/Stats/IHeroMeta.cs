namespace HappyMix.Application.Abstractions.Stats;

/// <summary>Rank groups for the meta search, as STRATZ rank brackets.</summary>
public enum MetaRank { All, HeraldGuardian, CrusaderArchon, LegendAncient, DivineImmortal }

/// <summary>Which games count: since the current patch came out, or the last 7 days.</summary>
public enum MetaPatch { Current, LastWeek }

/// <summary>How heroes are ordered: the meta score (win rate weighted by how sure it is), win rate or pick rate.</summary>
public enum MetaSort { Score, WinRate, PickRate }

/// <param name="Position">0 for any role, otherwise position 1 (carry) to 5 (hard support).</param>
public sealed record MetaFilter(MetaPatch Patch = MetaPatch.Current, int Position = 0, MetaRank Rank = MetaRank.All, MetaSort Sort = MetaSort.Score);

/// <param name="PickRate">Percent of matches with the hero in this role (any role: percent of all matches).</param>
public sealed record RankedHero(int HeroId, string Name, string? Image, int Games, int Wins, double PickRate)
{
    public double WinRate => Games == 0 ? 0 : Wins * 100.0 / Games;

    /// <summary>
    /// The lower bound of the 95% Wilson interval of the win rate, in percent: a high win rate over many games scores
    /// above the same rate over few, so popular strong heroes lead and lucky rare picks don't.
    /// </summary>
    public double Score
    {
        get
        {
            if (Games == 0)
                return 0;
            const double z = 1.96;
            double p = (double)Wins / Games, n = Games;
            return (p + z * z / (2 * n) - z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n))) / (1 + z * z / n) * 100;
        }
    }
}

/// <summary>
/// Hero win and pick rates for one filter, from STRATZ ranked All Pick games. <see cref="Heroes"/> holds every hero
/// with enough games, ordered by score; <see cref="Since"/> is when the counted games start.
/// </summary>
public sealed record HeroMeta(MetaFilter Filter, string? PatchName, DateTimeOffset? Since, DateTimeOffset FetchedAt, long Matches,
    IReadOnlyList<RankedHero> Heroes);

/// <summary>STRATZ hero statistics (needs <c>Stratz:ApiKey</c>).</summary>
public interface IHeroMetaSource
{
    bool Enabled { get; }
    Task<StatsFetch<HeroMeta>> GetHeroMetaAsync(MetaFilter filter, CancellationToken cancellationToken);
}
