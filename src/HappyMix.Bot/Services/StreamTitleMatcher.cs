using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HappyMix.Domain.Esports;

namespace HappyMix.Bot.Services;

/// <summary>
/// Decides whether a stream title is about a match: every team must be named in it. Each team is recognised by its full
/// name, its name without filler words ("Team Spirit" → "Spirit", "FaZe Clan" → "FaZe", "G2 Esports" → "G2") and its
/// acronym ("NAVI"). Titles and names are compared case- and accent-insensitively, punctuation counts as a space, and
/// spaces are optional, so "Na'Vi", "NaVi", "Natus-Vincere" and "natus vincere" all match. A name only matches as a whole
/// word: "Liquid" matches "Team Liquid vs NAVI" but "NAVI" does not match "navigation".
/// </summary>
public sealed class StreamTitleMatcher
{
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "team", "esports", "esport", "e", "sports", "gaming", "clan", "club", "gg", "org", "the"
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private readonly Regex[] _teams;

    public StreamTitleMatcher(IEnumerable<EsportsOpponent> teams) =>
        _teams = teams.Select(Pattern).OfType<Regex>().ToArray();

    /// <summary>True when every team with a usable name appears in <paramref name="title"/>; true when there are none.</summary>
    public bool Matches(string? title)
    {
        if (_teams.Length == 0)
            return true;
        if (string.IsNullOrWhiteSpace(title))
            return false;
        string text = Normalize(title);
        try
        {
            return _teams.All(team => team.IsMatch(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The variants a team is recognised by, normalized (for tests and diagnostics).</summary>
    public static IReadOnlyList<string> Variants(EsportsOpponent team)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            if (value is null)
                return;
            string normalized = Normalize(value);
            // One- or two-character names other than acronyms like "G2" would match far too much.
            if (normalized.Replace(" ", "", StringComparison.Ordinal).Length >= 2 && !variants.Contains(normalized))
                variants.Add(normalized);
        }

        Add(team.Name);
        var words = Normalize(team.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var core = words.Where(word => !Filler.Contains(word)).ToArray();
        if (core.Length > 0 && core.Length < words.Length)
            Add(string.Join(' ', core));
        Add(team.Acronym);
        return variants;
    }

    private static Regex? Pattern(EsportsOpponent team)
    {
        var variants = Variants(team);
        if (variants.Count == 0)
            return null;
        // Every character may be followed by an optional space, so spacing and punctuation inside a name don't matter.
        string alternatives = string.Join('|', variants.OrderByDescending(item => item.Length).Select(variant =>
            string.Join(@"\s?", variant.Replace(" ", "", StringComparison.Ordinal).Select(character => Regex.Escape(character.ToString())))));
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alternatives})(?![\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant, Timeout);
    }

    /// <summary>Lower case, accents removed, every run of non-letters/digits turned into one space.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = true;
        foreach (char character in text.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }
        return builder.ToString().TrimEnd();
    }
}
