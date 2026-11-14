using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Application.Guilds;

/// <summary>
/// Linked-account families in one server. Each family is one HappyMix member, identified by its primary account.
/// Discord permissions are never derived from here; they always come from the account making a request.
/// </summary>
public sealed class MemberLinks
{
    private readonly Dictionary<ulong, ulong> primaryOf;
    private readonly Dictionary<ulong, ulong[]> alternatesOf;

    private MemberLinks(IEnumerable<AccountLink> links)
    {
        var list = links.ToArray();
        primaryOf = list.ToDictionary(link => link.AlternateId, link => link.PrimaryId);
        alternatesOf = list.GroupBy(link => link.PrimaryId)
            .ToDictionary(group => group.Key, group => group.OrderBy(link => link.LinkedAt).Select(link => link.AlternateId).ToArray());
    }

    public static MemberLinks None { get; } = new([]);

    public static MemberLinks From(IEnumerable<AccountLink> links) => new(links);

    /// <summary>The shared member identity (the primary account) for any account in a family.</summary>
    public ulong Member(ulong account) => primaryOf.TryGetValue(account, out ulong primary) ? primary : account;

    public ulong[] Alternates(ulong account) => alternatesOf.TryGetValue(Member(account), out var alternates) ? alternates : [];

    /// <summary>The primary account followed by its alternates.</summary>
    public ulong[] Accounts(ulong account) => [Member(account), .. Alternates(account)];

    public bool IsLinked(ulong account) => primaryOf.ContainsKey(account) || alternatesOf.ContainsKey(account);

    public bool Same(ulong first, ulong second) => Member(first) == Member(second);

    public bool Includes(IEnumerable<ulong> accounts, ulong account)
    {
        ulong member = Member(account);
        return accounts.Any(item => Member(item) == member);
    }

    /// <summary>Distinct member identities, keeping first-seen order.</summary>
    public ulong[] Members(IEnumerable<ulong> accounts) => accounts.Select(Member).Distinct().ToArray();

    /// <summary>One recorded account per group member, preserving the earliest stored membership.</summary>
    public ulong[] Representatives(IEnumerable<ulong> accounts) => accounts.DistinctBy(Member).ToArray();
}

internal static class MemberLinkLoading
{
    public static async Task<MemberLinks> LoadAsync(this IAccountLinkStore store, ulong guildId) =>
        MemberLinks.From(await store.ListAsync(guildId));
}
