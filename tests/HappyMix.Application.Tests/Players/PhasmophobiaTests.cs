using HappyMix.Application.Phasmophobia;

namespace HappyMix.Application.Tests.Players;

/// <summary>The Phasmophobia evidence finder and ghost database.</summary>
public sealed class PhasmophobiaTests
{
    private static Ghost G(string name, string[] evidence, string[]? forced = null, string? extra = null) =>
        new(name.ToLowerInvariant(), name, evidence, forced ?? [], extra is null ? [] : [new ExtraEvidence(extra, "extra")], "", [], new("50%", ""), new("1.7 m/s", []),
            "", "", [], [], [], [], [], false, [], "test", []);

    // Made-up ghosts so these tests check the rules, not the game data.
    private static readonly Ghost Plain = G("Plain", ["emf", "uv", "writing"]);
    private static readonly Ghost Forced = G("Forced", ["dots", "uv", "spiritbox"], forced: ["dots"]);
    private static readonly Ghost Mimic = G("Mimic", ["spiritbox", "uv", "freezing"], extra: "orb");
    private static readonly Ghost Orby = G("Orby", ["orb", "freezing", "writing"]);
    private static readonly Ghost[] All = [Plain, Forced, Mimic, Orby];

    private static FinderInput Input(int count, params (string Evidence, EvidenceMark Mark)[] marks) =>
        new(marks.ToDictionary(item => item.Evidence, item => item.Mark), count);

    private static string[] Possible(FinderInput input, IEnumerable<Ghost>? ghosts = null) =>
        EvidenceFinder.Evaluate(ghosts ?? All, input).Where(result => result.Possible).Select(result => result.Ghost.Name).ToArray();

    private const EvidenceMark Yes = EvidenceMark.Confirmed, No = EvidenceMark.RuledOut;

    [Fact]
    public void UnknownEvidenceNeverRulesAGhostOut()
    {
        Assert.Equal(["Plain", "Forced", "Mimic", "Orby"], Possible(Input(3)));
        Assert.Equal(["Plain", "Forced", "Mimic", "Orby"], Possible(Input(1)));
    }

    [Fact]
    public void ConfirmedAndRuledOutEvidenceFilterWithThreeEvidence()
    {
        Assert.Equal(["Plain", "Forced", "Mimic"], Possible(Input(3, ("uv", Yes))));
        Assert.Equal(["Plain"], Possible(Input(3, ("uv", Yes), ("spiritbox", No))));
        // With all three evidence on, a ghost always shows each of its evidence: ruling one out removes it.
        Assert.Equal(["Forced", "Mimic"], Possible(Input(3, ("emf", No), ("writing", No)), [Plain, Forced, Mimic]));
        var plain = EvidenceFinder.Evaluate(Plain, Input(3, ("uv", Yes)));
        Assert.Equal(["uv"], plain.Confirmed);
        Assert.Equal(["emf", "writing"], plain.StillToFind);
        Assert.Equal(1, plain.Matches);
    }

    [Fact]
    public void NightmareHidesOneEvidenceSoARuledOutOneCanBeTheHiddenOne()
    {
        // Two evidence: Plain can hide EMF, so EMF ruled out keeps it; ruling out two of its three removes it.
        Assert.Contains("Plain", Possible(Input(2, ("emf", No))));
        Assert.DoesNotContain("Plain", Possible(Input(2, ("emf", No), ("uv", No))));
        // Three confirmed evidence can't come from a two-evidence contract.
        Assert.DoesNotContain("Plain", Possible(Input(2, ("emf", Yes), ("uv", Yes), ("writing", Yes))));
        Assert.Equal("count", EvidenceFinder.Evaluate(Plain, Input(2, ("emf", Yes), ("uv", Yes), ("writing", Yes))).Reason!.Code);
        // Once two are confirmed, the third can't show: it isn't left to find.
        Assert.Empty(EvidenceFinder.Evaluate(Plain, Input(2, ("emf", Yes), ("uv", Yes))).StillToFind);
    }

    [Fact]
    public void InsanityShowsOneEvidence()
    {
        Assert.Equal(["Plain", "Mimic"], Possible(Input(1, ("uv", Yes))));
        Assert.Empty(Possible(Input(1, ("uv", Yes), ("emf", Yes)), [Plain]));
        Assert.Contains("Plain", Possible(Input(1, ("emf", No), ("uv", No))));
    }

    [Fact]
    public void ForcedEvidenceCantBeHidden()
    {
        // On Insanity the only evidence a Forced ghost shows is D.O.T.S.
        Assert.DoesNotContain("Forced", Possible(Input(1, ("uv", Yes))));
        Assert.DoesNotContain("Forced", Possible(Input(2, ("dots", No))));
        Assert.Equal(new FinderReason("forced", "dots"), EvidenceFinder.Evaluate(Forced, Input(2, ("dots", No))).Reason);
        Assert.Contains("Forced", Possible(Input(2, ("dots", Yes), ("uv", Yes))));
        // With the forced-evidence rule switched off it behaves like any ghost.
        Assert.Contains("Forced", Possible(Input(1, ("uv", Yes)) with { ForcedEvidence = false }));
        // With no evidence at all nothing is forced, and a ruled-out D.O.T.S. says nothing.
        Assert.Contains("Forced", Possible(Input(0, ("dots", No))));
    }

    [Fact]
    public void MimicOrbsDontCountTowardTheEvidenceNumber()
    {
        // Orbs on a three-evidence contract: four evidence seen, still The Mimic.
        Assert.Equal(["Mimic"], Possible(Input(3, ("orb", Yes), ("spiritbox", Yes), ("uv", Yes), ("freezing", Yes))));
        Assert.Contains("Mimic", Possible(Input(3, ("orb", Yes))));
        // Insanity: one real evidence plus the orbs.
        Assert.Contains("Mimic", Possible(Input(1, ("orb", Yes), ("uv", Yes))));
        // Orbs show even with no evidence at all, and always: ruling them out removes The Mimic.
        Assert.Equal(["Mimic"], Possible(Input(0, ("orb", Yes))));
        Assert.DoesNotContain("Mimic", Possible(Input(2, ("orb", No))));
        var mimic = EvidenceFinder.Evaluate(Mimic, Input(3, ("orb", Yes)));
        Assert.Equal(["orb"], mimic.Confirmed);
        Assert.Equal(1, mimic.Matches);
        Assert.Equal(["spiritbox", "uv", "freezing"], mimic.StillToFind);
    }

    [Fact]
    public void NoEvidenceKeepsEveryGhostUnlessSomethingShowed()
    {
        Assert.Equal(["Plain", "Forced", "Mimic", "Orby"], Possible(Input(0, ("emf", No), ("uv", No), ("dots", No), ("spiritbox", No))));
        // Anything but orbs seen on a no-evidence contract fits no ghost.
        Assert.Empty(Possible(Input(0, ("uv", Yes))));
        Assert.Empty(EvidenceFinder.Evaluate(Plain, Input(0)).StillToFind);
    }

    [Fact]
    public void CustomDifficultyTakesZeroToThreeEvidence()
    {
        for (int count = 0; count <= 3; count++)
        {
            var sets = EvidenceFinder.ShownSets(Plain, Input(count)).ToArray();
            Assert.All(sets, set => Assert.Equal(count, set.Count));
            Assert.Equal(count switch { 0 => 1, 1 => 3, 2 => 3, _ => 1 }, sets.Length);
        }
    }

    [Fact]
    public void ChangingTheEvidenceNumberRecalculates()
    {
        var marks = new[] { ("emf", No) };
        Assert.DoesNotContain("Plain", Possible(Input(3, marks)));
        Assert.Contains("Plain", Possible(Input(2, marks)));
    }

    [Fact]
    public void DatabaseHasAll30GhostsWithValidEvidence()
    {
        var ghosts = GhostDatabase.Ghosts;
        Assert.Equal(30, ghosts.Count);
        Assert.All(ghosts, ghost =>
        {
            Assert.Equal(3, ghost.Evidence.Count);
            Assert.Equal(3, ghost.Evidence.Distinct().Count());
            Assert.All(ghost.Evidence, id => Assert.NotNull(GhostDatabase.Evidence(id)));
        });

        var represented = ghosts.SelectMany(g => g.Evidence).Distinct().ToHashSet();
        Assert.Equal(7, represented.Count);
    }

    [Fact]
    public void RealDatabaseMimicAndForcedEvidenceWork()
    {
        var mimic = GhostDatabase.Find("mimic");
        Assert.NotNull(mimic);
        Assert.True(EvidenceFinder.Evaluate(mimic, Input(0, ("orb", Yes))).Possible);
        Assert.True(EvidenceFinder.Evaluate(mimic, Input(2, ("orb", Yes), ("uv", Yes), ("freezing", Yes))).Possible);

        var hantu = GhostDatabase.Find("hantu");
        Assert.NotNull(hantu);
        Assert.False(EvidenceFinder.Evaluate(hantu, Input(1, ("freezing", No))).Possible);
        Assert.True(EvidenceFinder.Evaluate(hantu, Input(2, ("uv", Yes))).Possible);
    }
}
