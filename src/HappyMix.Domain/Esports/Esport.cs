namespace HappyMix.Domain.Esports;

/// <summary>Identifies a discipline independently of provider-specific numeric identifiers.</summary>
public enum Esport
{
    /// <summary>Counter-Strike 2, exposed under PandaScore's csgo API.</summary>
    CounterStrike2,
    /// <summary>Dota 2.</summary>
    Dota2
}

/// <summary>Provides user-facing discipline labels.</summary>
public static class EsportLabels
{
    /// <summary>Returns the discipline's readable name.</summary>
    /// <param name="game">The supported discipline.</param>
    /// <returns>The Discord display label.</returns>
    public static string DisplayName(this Esport game) => game switch
    {
        Esport.CounterStrike2 => "Counter-Strike 2",
        Esport.Dota2 => "Dota 2",
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };
}
