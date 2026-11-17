using System.Globalization;
using System.Resources;
using HappyMix.Bot.Web;

namespace HappyMix.Bot.Services;

internal static class NotificationText
{
    private static readonly ResourceManager Resources = new("HappyMix.Bot.Resources.Web.DashboardResources", typeof(DashboardResources).Assembly);
    public static string Get(string language, string key, params object[] values)
    {
        var culture = CultureInfo.GetCultureInfo(language is "uk" or "ru" ? language : "en");
        return string.Format(culture, Resources.GetString(key, culture) ?? key, values);
    }
}
