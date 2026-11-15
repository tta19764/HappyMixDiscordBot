using System.Globalization;
using HappyMix.Bot.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HappyMix.Application.Tests.Localization;

public sealed class DashboardLocalizationTests
{
    [Theory]
    [InlineData("en", "Inherit", "2 members")]
    [InlineData("uk", "За замовчуванням", "Учасників: 2")]
    [InlineData("ru", "По умолчанию", "Участников: 2")]
    [InlineData("fr", "Inherit", "2 members")]
    public void StableKeysAndMessageBoundaryUseSelectedLanguage(string culture, string inherit, string members)
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

            Assert.Equal(inherit, text["Inherit"].Value);
            Assert.False(text["Inherit"].ResourceNotFound);
            Assert.Equal(members, text["Arg0_members", 2].Value);
            Assert.Equal(members, text.Message("{0} members", 2).Value);
            Assert.Equal("Uncatalogued message", text.Message("Uncatalogued message").Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
