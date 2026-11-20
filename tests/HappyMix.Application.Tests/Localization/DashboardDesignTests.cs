using System.Globalization;
using System.Text.RegularExpressions;
using HappyMix.Bot.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HappyMix.Application.Tests.Localization;

/// <summary>The dashboard's own icon set and the side panel's text, in every language.</summary>
public sealed partial class DashboardDesignTests
{
    private static readonly string[] TagNames = ["games", "series", "films", "anime", "ranked"];

    [Fact]
    public void EveryIconAViewNamesIsInTheSet()
    {
        var names = ViewFiles().SelectMany(file => IconName().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value)).Distinct().ToArray();
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(DashboardIcons.Has(name), $"Unknown icon \"{name}\""));
        Assert.All(TagNames, tag => Assert.Contains("<path", DashboardIcons.Tag(tag).ToString()));
    }

    [Fact]
    public void ViewsUseTheIconSetInsteadOfEmoji()
    {
        // The notification preview mirrors the Discord message, which keeps its emoji.
        var offenders = ViewFiles().Where(file => !file.EndsWith("NotificationSettings.cshtml", StringComparison.Ordinal))
            .Where(file => Emoji().IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).ToArray();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("en", "Collapse menu")]
    [InlineData("uk", "Згорнути меню")]
    [InlineData("ru", "Свернуть меню")]
    public void SidePanelTextExistsInEveryLanguage(string culture, string collapse)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization(options => options.ResourcesPath = "Resources");
            using var provider = services.BuildServiceProvider();
            var text = provider.GetRequiredService<IStringLocalizer<DashboardResources>>();

            Assert.Equal(collapse, text["Side_Collapse"].Value);
            string[] keys = ["Side_Menu", "Side_SwitchServer", "Side_Server", "Side_Admin", "Side_Expand", "Signed_in", "HappyMix_home",
                "Refresh_list", "Set_availability", "No_hours_set_for_today", "Light", "Dark", "System", "Theme", "Language", "Sign_out", "Info"];
            Assert.All(keys, key => Assert.False(text[key].ResourceNotFound, key));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static string[] ViewFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "HappyMix.Bot", "Views")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Directory.GetFiles(Path.Combine(directory.FullName, "src", "HappyMix.Bot", "Views"), "*.cshtml", SearchOption.AllDirectories);
    }

    [GeneratedRegex("DashboardIcons\\.Render\\(\"([a-z-]+)\"")]
    private static partial Regex IconName();

    [GeneratedRegex("[\\u2600-\\u27BF\\u23F3\\u231B\\u2B50]|\\uD83C[\\uDF00-\\uDFFF]|\\uD83D[\\uDC00-\\uDEFF]|\\uD83E[\\uDD00-\\uDEFF]")]
    private static partial Regex Emoji();
}
