using HappyMix.Bot.Web;

namespace HappyMix.Application.Tests.Localization;

/// <summary>The weekly day editor posts a type (free time or do not disturb) and a certainty per range.</summary>
public sealed class WeeklyRangeFormTests
{
    [Fact]
    public void TypeAndCertaintyBecomeOneKindPerRange()
    {
        Assert.Equal(["definite", "dnd", "tentative", "definite"],
            DashboardController.RangeKinds(["free", "dnd", "free", "free"], ["definite", "tentative", "tentative", "junk"]));
        // A do-not-disturb row ignores its (hidden) certainty; a missing certainty counts as definite.
        Assert.Equal(["dnd", "definite"], DashboardController.RangeKinds(["dnd", "free"], ["tentative"]));
        // Pages from before the split post only the old kind list.
        Assert.Equal(["tentative", "dnd"], DashboardController.RangeKinds(null, ["tentative", "dnd"]));
        Assert.Empty(DashboardController.RangeKinds(null, null));
    }

    [Fact]
    public void TheSideNavCookieRoundTrips()
    {
        var moved = SideNavOrder.Move([], "votes", -1);
        Assert.Equal(moved, SideNavOrder.Parse(string.Join(SideNavOrder.Separator, moved)));
    }
}
