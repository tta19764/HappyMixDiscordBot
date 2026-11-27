using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappyMix.Application.Phasmophobia;

/// <summary>How sure a mechanic is: always, only under conditions, by chance, or a measured probability.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MechanicKind>))]
public enum MechanicKind { Guaranteed, Conditional, Random, Statistical }

/// <summary>One fact about a ghost and how sure it is.</summary>
public sealed record GhostFact(string Text, MechanicKind Kind);

/// <summary>Evidence a ghost shows on top of its three standard ones, like The Mimic's Ghost Orbs.</summary>
public sealed record ExtraEvidence(string Evidence, string Text);

public sealed record HuntThreshold(string Value, string Notes);

public sealed record GhostSpeed(string Base, IReadOnlyList<GhostFact> Conditional);

/// <summary>
/// A Phasmophobia ghost type. <see cref="Evidence"/> is always its three standard evidence ids;
/// <see cref="ForcedEvidence"/> can't be hidden on contracts with fewer evidence; <see cref="ExtraEvidence"/> is
/// shown on top and never counts toward the contract's evidence number.
/// </summary>
/// <param name="Clues">Two or three short tells shown on the card and in the finder.</param>
/// <param name="Distinctive">Has a behaviour-only tell, useful when the contract gives no evidence.</param>
public sealed record Ghost(string Id, string Name, IReadOnlyList<string> Evidence, IReadOnlyList<string> ForcedEvidence,
    IReadOnlyList<ExtraEvidence> ExtraEvidence, string Summary, IReadOnlyList<string> Clues, HuntThreshold HuntThreshold, GhostSpeed Speed, string Strength, string Weakness,
    IReadOnlyList<GhostFact> Abilities, IReadOnlyList<GhostFact> Events, IReadOnlyList<GhostFact> Hunt, IReadOnlyList<string> Detection,
    IReadOnlyList<GhostFact> Notes, bool Distinctive, IReadOnlyList<string> Sources, string VerifiedVersion, IReadOnlyList<string> Unverified)
{
    /// <summary>Every evidence that can be seen with this ghost: its standard three and its extra ones.</summary>
    public IEnumerable<string> AllEvidence => Evidence.Concat(ExtraEvidence.Select(extra => extra.Evidence)).Distinct();
}

/// <summary>An evidence type, in the order the game's journal lists them.</summary>
public sealed record EvidenceType(string Id, string Name, string Icon);

/// <summary>A difficulty preset and how many evidence types its contracts give.</summary>
public sealed record DifficultyPreset(string Id, int EvidenceCount);

/// <summary>
/// The ghost database, read once from the embedded ghosts.json (English). ghosts.uk.json and ghosts.ru.json carry the
/// same ghosts' text in Ukrainian and Russian; the evidence, forced and extra evidence always come from the English
/// file, so a translation can't change what the evidence finder decides.
/// </summary>
public static class GhostDatabase
{
    public static readonly IReadOnlyList<EvidenceType> EvidenceTypes =
    [
        new("emf", "EMF Level 5", "phasmo-emf"),
        new("dots", "D.O.T.S. Projector", "phasmo-dots"),
        new("uv", "Ultraviolet", "phasmo-uv"),
        new("freezing", "Freezing Temperatures", "phasmo-freezing"),
        new("orb", "Ghost Orb", "phasmo-orb"),
        new("writing", "Ghost Writing", "phasmo-writing"),
        new("spiritbox", "Spirit Box", "phasmo-spiritbox")
    ];

    /// <summary>Presets; "custom" lets the player pick 0–3 evidence.</summary>
    public static readonly IReadOnlyList<DifficultyPreset> Difficulties =
    [
        new("amateur", 3), new("intermediate", 3), new("professional", 3), new("nightmare", 2), new("insanity", 1), new("custom", 3)
    ];

    /// <summary>The languages with their own ghost text besides English.</summary>
    public static readonly IReadOnlyList<string> Languages = ["uk", "ru"];

    private static readonly Lazy<IReadOnlyList<Ghost>> English = new(() => Read("ghosts.json") ?? throw new InvalidOperationException("ghosts.json isn't embedded."));
    private static readonly Dictionary<string, Lazy<IReadOnlyList<Ghost>>> Translated = Languages.ToDictionary(language => language,
        language => new Lazy<IReadOnlyList<Ghost>>(() => Translate(Read($"ghosts.{language}.json") ?? [], language)));

    /// <summary>Every ghost, alphabetical, in English.</summary>
    public static IReadOnlyList<Ghost> Ghosts => English.Value;

    /// <summary>Every ghost with its text in the culture's language when there is one (English otherwise), alphabetical in it.</summary>
    public static IReadOnlyList<Ghost> For(System.Globalization.CultureInfo culture) =>
        Translated.TryGetValue(culture.TwoLetterISOLanguageName, out var ghosts) ? ghosts.Value : English.Value;

    public static Ghost? Find(string? id) => Ghosts.FirstOrDefault(ghost => string.Equals(ghost.Id, id, StringComparison.OrdinalIgnoreCase));

    public static EvidenceType? Evidence(string id) => EvidenceTypes.FirstOrDefault(type => type.Id == id);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true };

    internal static Ghost[]? Read(string file)
    {
        using var stream = typeof(GhostDatabase).Assembly.GetManifestResourceStream("HappyMix.Phasmophobia." + file);
        if (stream is null)
            return null;
        var ghosts = JsonSerializer.Deserialize<Ghost[]>(stream, Json) ?? [];
        return ghosts.OrderBy(ghost => ghost.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The English ghosts with the translated text laid over them; a ghost missing from the translation stays English.</summary>
    private static Ghost[] Translate(IReadOnlyList<Ghost> translated, string language)
    {
        var byId = translated.ToDictionary(ghost => ghost.Id);
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo(language), ignoreCase: true);
        return English.Value.Select(ghost => byId.TryGetValue(ghost.Id, out var text) ? Overlay(ghost, text) : ghost)
            .OrderBy(ghost => ghost.Name, comparer).ToArray();
    }

    internal static Ghost Overlay(Ghost english, Ghost text) => text with
    {
        Evidence = english.Evidence,
        ForcedEvidence = english.ForcedEvidence,
        ExtraEvidence = english.ExtraEvidence.Select(extra =>
            extra with { Text = text.ExtraEvidence.FirstOrDefault(item => item.Evidence == extra.Evidence)?.Text ?? extra.Text }).ToArray(),
        Distinctive = english.Distinctive,
        Sources = english.Sources,
        VerifiedVersion = english.VerifiedVersion
    };
}
