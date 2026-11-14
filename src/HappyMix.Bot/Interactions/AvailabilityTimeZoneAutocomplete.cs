using Discord;
using Discord.Interactions;

namespace HappyMix.Bot.Interactions;

public sealed class AvailabilityTimeZoneAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        string search = (autocompleteInteraction.Data.Current.Value as string)?.Trim() ?? "";
        string normalized = search.Replace('_', ' ');
        var matches = Names
            .Where(id => id.Replace('_', ' ').Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => id.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .ThenBy(id => id, StringComparer.Ordinal)
            .Take(25)
            .Select(id => new AutocompleteResult(id, id));
        return Task.FromResult(AutocompletionResult.FromSuccess(matches));
    }

    internal static IReadOnlyList<string> Names { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.HasIanaId ? zone.Id :
            TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? id) ? id : null)
        .OfType<string>()
        .Append("UTC")
        .Where(id => id.Length <= 100)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
