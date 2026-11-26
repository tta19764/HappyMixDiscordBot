using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Application.Players;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Live;

/// <summary>A member of the group playing now (only people who share with the group).</summary>
public sealed record LivePlayer(ulong UserId, string Game, DateTimeOffset Since);

/// <param name="Share">The viewer shares what they play with this group.</param>
/// <param name="Notify">The viewer gets DMs when members start a game.</param>
/// <param name="Recent">Sessions of sharing members from the last day, newest first.</param>
public sealed record GroupLive(bool Share, bool Notify, bool PostInChannel, bool CanManage, int Sharers, IReadOnlyList<LivePlayer> Playing,
    IReadOnlyList<PlaySession> Recent);

public sealed record GetGroupLiveQuery(GroupActor Actor, Guid GroupId) : IQuery<GroupLive>;

/// <summary>The viewer's choices for one group. Only members of the group can turn them on.</summary>
public sealed record SetLiveSharingCommand(GroupActor Actor, Guid GroupId, bool Share, bool Notify) : ICommand<GroupLive>;

/// <summary>The organizer (or a moderator) also posts Live notices in the group's notification channel.</summary>
public sealed record SetLiveChannelCommand(GroupActor Actor, Guid GroupId, bool Post) : ICommand<GroupLive>;

/// <summary>
/// What someone is playing now, from one source ("discord" status or "steam"); null when they stopped. Starting a game
/// notifies the groups they share with, when the game matters to the group and the cooldown has passed. Returns the
/// number of groups notified.
/// </summary>
public sealed record ReportPlayingCommand(ulong UserId, string? Game, string Source) : ICommand<int>;

/// <summary>Everyone who shares with a group, so the presence watcher can ignore everyone else.</summary>
public sealed record GetLiveSharersQuery : IQuery<IReadOnlyList<ulong>>;

/// <summary>Asks Steam what sharing members with a verified Steam account are playing, and reports it. Also prunes old sessions.</summary>
public sealed record SyncSteamPlayingCommand : ICommand<int>;

public static class LiveRules
{
    /// <summary>One notice per player and game per group in this window.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(2);
    /// <summary>How long ended sessions are kept for the Live tab.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public const int MaxGameLength = 120;
    /// <summary>
    /// Someone with hours set this close to now is free only inside them. Without hours this close, nothing is known,
    /// so they still get messages.
    /// </summary>
    public static readonly TimeSpan HoursAround = TimeSpan.FromHours(12);
}

internal sealed class LiveHandlers(ILiveStore store, IGuildGroupStore groups, IAccountLinkStore accountLinks, IPlayerGameStore players,
    ISteamDirectory steam, IEnumerable<ILiveNotifier> notifiers, IClock clock, ISender sender, IEnumerable<IAvailabilityStore> availability,
    IEnumerable<HappyMix.Application.Abstractions.Quiet.IQuietStore> quiet) :
    IQueryHandler<GetGroupLiveQuery, GroupLive>, ICommandHandler<SetLiveSharingCommand, GroupLive>, ICommandHandler<SetLiveChannelCommand, GroupLive>,
    ICommandHandler<ReportPlayingCommand, int>, IQueryHandler<GetLiveSharersQuery, IReadOnlyList<ulong>>, ICommandHandler<SyncSteamPlayingCommand, int>
{
    private static readonly ErrorDescriptor NotFound = new("Live.NotFound", "That group doesn't exist or you don't have access to it.");

    public async Task<Result<GroupLive>> Handle(GetGroupLiveQuery query, CancellationToken cancellationToken)
    {
        if (await DetailAsync(query.Actor, query.GroupId, cancellationToken) is not { CanViewRoster: true } detail)
            return Result.Failure<GroupLive>(NotFound);
        return await LiveAsync(query.Actor, detail);
    }

    public async Task<Result<GroupLive>> Handle(SetLiveSharingCommand command, CancellationToken cancellationToken)
    {
        if (await DetailAsync(command.Actor, command.GroupId, cancellationToken) is not { CanViewRoster: true } detail)
            return Result.Failure<GroupLive>(NotFound);
        var links = await accountLinks.LoadAsync(command.Actor.GuildId);
        if (!links.Includes(detail.Members, command.Actor.UserId) && !links.Same(detail.Summary.OrganizerId, command.Actor.UserId))
            return Result.Failure<GroupLive>(new ErrorDescriptor("Live.NotMember", "Only members of the group can share or get Live messages."));
        await store.SaveMembershipAsync(new LiveMembership(command.Actor.GuildId, command.GroupId, links.Member(command.Actor.UserId), command.Share, command.Notify));
        return await LiveAsync(command.Actor, detail);
    }

    public async Task<Result<GroupLive>> Handle(SetLiveChannelCommand command, CancellationToken cancellationToken)
    {
        if (await DetailAsync(command.Actor, command.GroupId, cancellationToken) is not { CanViewRoster: true } detail)
            return Result.Failure<GroupLive>(NotFound);
        if (!detail.Summary.CanManage)
            return Result.Failure<GroupLive>(new ErrorDescriptor("Live.Forbidden", "Only the organizer or a moderator can change this."));
        await store.SetChannelPostsAsync(command.Actor.GuildId, command.GroupId, command.Post);
        return await LiveAsync(command.Actor, detail);
    }

    public async Task<Result<int>> Handle(ReportPlayingCommand command, CancellationToken cancellationToken)
    {
        string? game = command.Game?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, LiveRules.MaxGameLength)] : null;
        var now = clock.UtcNow;
        var open = await store.GetOpenAsync(command.UserId, command.Source);
        if (open is not null && game is not null && GameKeys.Normalize(open.Game) == GameKeys.Normalize(game))
            return 0;
        if (open is not null)
            await store.EndAsync(open.Id, now);
        if (game is null)
            return 0;
        await store.StartAsync(command.UserId, game, command.Source, now);

        int notified = 0;
        foreach (var sharing in await store.ListSharingAsync(command.UserId))
        {
            try
            {
                if (await NoticeAsync(sharing, command.UserId, game, now) is not { } notice)
                    continue;
                await store.SaveAlertAsync(sharing.GuildId, sharing.GroupId, command.UserId, GameKeys.Normalize(game), now);
                foreach (var notifier in notifiers)
                    await notifier.NotifyAsync(notice, cancellationToken);
                notified++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best effort: one group's problem (a deleted channel, closed DMs) never stops the others.
            }
        }
        return notified;
    }

    public async Task<Result<IReadOnlyList<ulong>>> Handle(GetLiveSharersQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ListSharersAsync());

    public async Task<Result<int>> Handle(SyncSteamPlayingCommand command, CancellationToken cancellationToken)
    {
        await store.PruneAsync(clock.UtcNow - LiveRules.Retention);
        var accounts = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (ulong user in await store.ListSharersAsync())
            if (await players.GetSteamAsync(user) is { Verified: true } account)
                accounts[account.SteamId] = user; // only accounts proven to be the member's
        if (accounts.Count == 0)
            return 0;
        var playing = await steam.GetPlayingAsync(accounts.Keys, cancellationToken);
        int notified = 0;
        foreach (var (steamId, user) in accounts)
            notified += (await Handle(new ReportPlayingCommand(user, playing.GetValueOrDefault(steamId), "steam"), cancellationToken)).Value;
        return notified;
    }

    /// <summary>A notice when the player is still in the group, the game matters to it and the cooldown has passed.</summary>
    private async Task<LiveNotice?> NoticeAsync(LiveMembership sharing, ulong player, string game, DateTimeOffset now)
    {
        var group = (await groups.ListAsync(sharing.GuildId)).FirstOrDefault(item => item.Id == sharing.GroupId);
        var links = await accountLinks.LoadAsync(sharing.GuildId);
        if (group is null || !(links.Includes(group.Members, player) || links.Same(group.OrganizerId, player)))
            return null;
        if (await store.LastAlertAsync(sharing.GuildId, sharing.GroupId, player, GameKeys.Normalize(game)) is { } last && now - last < LiveRules.Cooldown)
            return null;

        var members = group.Members.Append(group.OrganizerId).Select(links.Member).Distinct().Where(member => member != links.Member(player)).ToArray();
        var marks = await players.ListForAsync(members.SelectMany(links.Accounts).Distinct().ToArray());
        bool Marked(ulong member, params GamePreference[] preferences) => marks.Any(mark =>
            links.Same(mark.UserId, member) && preferences.Contains(mark.Preference) && GameKeys.Same(mark, game, null, null));
        bool groupGame = (await groups.ListGamesAsync(sharing.GuildId)).Any(item => item.GroupId == group.Id && GameKeys.Normalize(item.Name) == GameKeys.Normalize(game));
        if (!groupGame && !members.Any(member => Marked(member, GamePreference.Want, GamePreference.Can)))
            return null;

        // Only people who could join now are messaged or named: not in do-not-disturb, and free now when they set hours.
        var reachable = await ReachableAsync(sharing.GuildId, links, members, now);
        var memberships = await store.ListMembershipsAsync(sharing.GuildId, sharing.GroupId);
        var recipients = memberships.Where(item => item.Notify && members.Contains(links.Member(item.UserId)) && reachable.Contains(links.Member(item.UserId)))
            .Select(item => item.UserId).Distinct().ToArray();
        bool channel = await store.GetChannelPostsAsync(sharing.GuildId, sharing.GroupId);
        if (recipients.Length == 0 && !channel)
            return null;
        return new LiveNotice(sharing.GuildId, sharing.GroupId, group.Name, player, game,
            members.Where(member => Marked(member, GamePreference.Want) && reachable.Contains(member)).ToArray(), recipients, channel);
    }

    /// <summary>
    /// Members (primary accounts) who can be messaged at <paramref name="now"/>: none of their accounts is in
    /// do-not-disturb, and they are free now or set no hours within <see cref="LiveRules.HoursAround"/> of now.
    /// </summary>
    private async Task<HashSet<ulong>> ReachableAsync(ulong guildId, MemberLinks links, ulong[] members, DateTimeOffset now)
    {
        var quietHours = await BoardRows.QuietAsync(quiet, links, members);
        var reachable = members.Where(member => quietHours.GetValueOrDefault(member) is not { IsEmpty: false } schedule
            || HappyMix.Application.Quiet.QuietHours.Until(schedule, now) is null).ToHashSet();
        if (availability.FirstOrDefault() is not { } hours || reachable.Count == 0)
            return reachable;

        var from = now - LiveRules.HoursAround;
        var until = now + LiveRules.HoursAround;
        var snapshot = await hours.ListForMembersAsync(guildId, links.Members(reachable), from, until);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var ranges = snapshot.Windows.Select(item => (Member: links.Member(item.UserId), item.Window.Start, item.Window.End))
            .Concat(snapshot.Weekly.SelectMany(item => WeeklyOccurrences.ForDate(item.Weekly, today)
                .Select(occurrence => (Member: links.Member(item.UserId), occurrence.Start, occurrence.End))))
            .Where(range => range.Start < until && range.End > from)
            .ToLookup(range => range.Member);
        reachable.RemoveWhere(member => ranges[member].Any() && !ranges[member].Any(range => range.Start <= now && now < range.End));
        return reachable;
    }

    private async Task<GroupLive> LiveAsync(GroupActor actor, GroupDetail detail)
    {
        var links = await accountLinks.LoadAsync(actor.GuildId);
        var memberships = await store.ListMembershipsAsync(actor.GuildId, detail.Summary.Id);
        var mine = memberships.FirstOrDefault(item => links.Same(item.UserId, actor.UserId));
        var sharers = memberships.Where(item => item.Share).Select(item => item.UserId).Distinct().ToArray();
        var sessions = await store.ListSessionsAsync(sharers, clock.UtcNow.AddDays(-1));
        // One line per person and game: Discord and Steam may both see the same session.
        var playing = sessions.Where(session => session.EndedAt is null)
            .GroupBy(session => (session.UserId, Game: GameKeys.Normalize(session.Game)))
            .Select(group => group.OrderBy(session => session.StartedAt).First())
            .Select(session => new LivePlayer(session.UserId, session.Game, session.StartedAt))
            .OrderBy(player => player.Since).ToArray();
        var recent = sessions.Where(session => session.EndedAt is not null).OrderByDescending(session => session.EndedAt).Take(20).ToArray();
        return new GroupLive(mine?.Share == true, mine?.Notify == true, await store.GetChannelPostsAsync(actor.GuildId, detail.Summary.Id),
            detail.Summary.CanManage, sharers.Length, playing, recent);
    }

    private async Task<GroupDetail?> DetailAsync(GroupActor actor, Guid groupId, CancellationToken cancellationToken) =>
        await sender.Send(new GetGroupDetailQuery(actor, groupId), cancellationToken) is { IsSuccess: true } result ? result.Value : null;
}
