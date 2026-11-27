namespace HappyMix.Application.Phasmophobia;

/// <summary>What the player knows about one evidence type.</summary>
public enum EvidenceMark { Unknown, Confirmed, RuledOut }

/// <summary>
/// The finder's input: the marks per evidence id, how many evidence types the contract gives (0–3), and whether
/// ghosts' forced evidence is kept on reduced-evidence contracts.
/// </summary>
public sealed record FinderInput(IReadOnlyDictionary<string, EvidenceMark> Marks, int EvidenceCount, bool ForcedEvidence = true)
{
    public EvidenceMark Mark(string evidence) => Marks.GetValueOrDefault(evidence);
    public IEnumerable<string> Confirmed => Marks.Where(item => item.Value == EvidenceMark.Confirmed).Select(item => item.Key);
    public IEnumerable<string> RuledOut => Marks.Where(item => item.Value == EvidenceMark.RuledOut).Select(item => item.Key);
}

/// <summary>Why a ghost was ruled out: the code names the case, <see cref="Evidence"/> the evidence it is about.</summary>
/// <param name="Code">
/// lacks: a confirmed evidence it never shows; forced: a ruled-out evidence it can't hide; has: a ruled-out evidence
/// it always shows on this contract; count: the marks don't fit any set of evidence this contract can show.
/// </param>
public sealed record FinderReason(string Code, string? Evidence = null);

/// <summary>
/// One ghost against the marks. <see cref="Matches"/> counts confirmed evidence it explains; it is not a probability.
/// <see cref="StillToFind"/> is its evidence nobody marked yet that this contract could still show.
/// </summary>
public sealed record FinderResult(Ghost Ghost, bool Possible, IReadOnlyList<string> Confirmed, IReadOnlyList<string> StillToFind, int Matches,
    FinderReason? Reason);

/// <summary>
/// Which ghosts fit the evidence. A contract with N evidence shows N of the ghost's three standard evidence types
/// (all three when N ≥ 3, none when N = 0); forced evidence is always among the shown ones. Extra evidence (The
/// Mimic's Ghost Orbs) shows on top, whatever N is, and never counts toward it. A ghost stays possible when some
/// set of evidence the contract can show contains every confirmed evidence and none of the ruled-out ones.
/// </summary>
public static class EvidenceFinder
{
    public static IReadOnlyList<FinderResult> Evaluate(IEnumerable<Ghost> ghosts, FinderInput input) =>
        ghosts.Select(ghost => Evaluate(ghost, input)).ToArray();

    public static FinderResult Evaluate(Ghost ghost, FinderInput input)
    {
        var confirmed = input.Confirmed.ToHashSet();
        var ruledOut = input.RuledOut.ToHashSet();
        var shown = ShownSets(ghost, input).ToArray();
        // Every set of evidence the contract can show, with the extra evidence that always shows.
        var fitting = shown.Where(set => confirmed.IsSubsetOf(set) && !ruledOut.Overlaps(set)).ToArray();
        var explained = confirmed.Where(evidence => ghost.AllEvidence.Contains(evidence)).ToArray();
        bool possible = fitting.Length > 0;
        var stillToFind = possible
            ? ghost.Evidence.Where(evidence => input.Mark(evidence) == EvidenceMark.Unknown && fitting.Any(set => set.Contains(evidence))).ToArray()
            : [];
        return new FinderResult(ghost, possible, explained, stillToFind, explained.Length, possible ? null : Why(ghost, input, confirmed, ruledOut, shown));
    }

    /// <summary>The evidence sets a contract can show for the ghost, each with its extra evidence added.</summary>
    public static IEnumerable<HashSet<string>> ShownSets(Ghost ghost, FinderInput input)
    {
        int count = Math.Clamp(input.EvidenceCount, 0, ghost.Evidence.Count);
        var forced = input.ForcedEvidence && count > 0 ? ghost.ForcedEvidence.Where(ghost.Evidence.Contains).ToArray() : [];
        var extra = ghost.ExtraEvidence.Select(item => item.Evidence).ToArray();
        foreach (var subset in Subsets(ghost.Evidence, count))
        {
            // Forced evidence is always shown; with fewer slots than forced evidence, the slots are all forced ones.
            bool keepsForced = forced.Length <= count ? forced.All(subset.Contains) : subset.All(forced.Contains);
            if (keepsForced)
                yield return subset.Concat(extra).ToHashSet();
        }
    }

    private static IEnumerable<string[]> Subsets(IReadOnlyList<string> items, int size)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }
        for (int i = 0; i <= items.Count - size; i++)
            foreach (var rest in Subsets(items.Skip(i + 1).ToArray(), size - 1))
                yield return [items[i], .. rest];
    }

    private static FinderReason Why(Ghost ghost, FinderInput input, HashSet<string> confirmed, HashSet<string> ruledOut, HashSet<string>[] shown)
    {
        if (confirmed.FirstOrDefault(evidence => !ghost.AllEvidence.Contains(evidence)) is { } missing)
            return new("lacks", missing);
        if (ruledOut.FirstOrDefault(evidence => shown.All(set => set.Contains(evidence))) is { } always)
            return new(input.ForcedEvidence && input.EvidenceCount > 0 && ghost.ForcedEvidence.Contains(always) && input.EvidenceCount < 3 ? "forced" : "has", always);
        return new("count");
    }
}
