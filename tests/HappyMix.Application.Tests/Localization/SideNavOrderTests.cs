using HappyMix.Bot.Web;

namespace HappyMix.Application.Tests.Localization;

/// <summary>The side panel order saved per browser: reading the cookie, applying it and moving one link.</summary>
public sealed class SideNavOrderTests
{
    private static string[] Keys(IEnumerable<SideNavLink> links) => links.Select(link => link.Key).ToArray();

    [Fact]
    public void NoSavedOrderKeepsTheDefault()
    {
        Assert.Equal(Keys(SideNavOrder.Server), Keys(SideNavOrder.Apply(SideNavOrder.Server, [])));
        Assert.Empty(SideNavOrder.Parse(null));
        Assert.Empty(SideNavOrder.Parse(new string('a', 401)));
    }

    [Fact]
    public void TheSavedOrderWinsAndUnknownOrMissingLinksAreHandled()
    {
        var order = SideNavOrder.Parse("votes. groups.hacker.votes..logs");
        Assert.Equal(["votes", "groups", "logs"], order);
        // Links not in the saved order keep their default slots; the saved ones fill the rest in their saved order.
        Assert.Equal(["availability", "events", "game-news", "game-features", "votes", "my-games", "groups", "links"],
            Keys(SideNavOrder.Apply(SideNavOrder.Server, order)));
        Assert.Equal(["settings", "logs"], Keys(SideNavOrder.Apply(SideNavOrder.Admin, order))); // only one of two saved: no change
    }

    [Fact]
    public void MovesStayWithinTheirPartOfThePanel()
    {
        var moved = SideNavOrder.Move([], "groups", -1);
        Assert.Equal(["availability", "events", "game-news", "groups", "game-features", "my-games", "votes", "links", "settings", "logs"], moved);
        // The first server link can't go up, the last can't go into the admin part.
        Assert.Equal(moved, SideNavOrder.Move(moved, "availability", -1));
        Assert.Equal(moved, SideNavOrder.Move(moved, "links", 1));
        Assert.Equal(["logs", "settings"], SideNavOrder.Move(moved, "settings", 1)[^2..]);
        Assert.Equal(moved, SideNavOrder.Move(moved, "nothing", 1));
    }
}
