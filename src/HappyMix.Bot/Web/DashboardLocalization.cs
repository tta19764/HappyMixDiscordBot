using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

[assembly: NeutralResourcesLanguage("en", UltimateResourceFallbackLocation.Satellite)]

namespace HappyMix.Bot.Web;

/// <summary>Localizes messages supplied by application errors, command metadata and view models.</summary>
public static class DashboardLocalization
{
    private static readonly Dictionary<string, string> MessageKeys = LoadMessageKeys();

    public static LocalizedString Message(this IStringLocalizer localizer, string message, params object[] arguments)
    {
        string key = MessageKeys.GetValueOrDefault(message) ?? message;
        return arguments.Length == 0 ? localizer[key] : localizer[key, arguments];
    }

    private static Dictionary<string, string> LoadMessageKeys()
    {
        var manager = new ResourceManager("HappyMix.Bot.Resources.Web.DashboardResources", typeof(DashboardResources).Assembly);
        var resources = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)
            ?? throw new InvalidOperationException("The English dashboard resource catalog is missing.");
        return resources.Cast<DictionaryEntry>().ToDictionary(
            entry => (string)entry.Value!, entry => (string)entry.Key, StringComparer.Ordinal);
    }
}
