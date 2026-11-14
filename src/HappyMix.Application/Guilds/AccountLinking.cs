using System.Security.Cryptography;
using System.Text;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>Checks live server membership through Discord. Implemented by the bot adapter.</summary>
public interface IGuildMemberGateway
{
    Task<bool> IsAdministratorAsync(ulong guildId, ulong userId);
    Task<bool> IsMemberAsync(ulong guildId, ulong userId);
}

public sealed record CreatedLinkRequest(Guid RequestId, string Token, DateTimeOffset ExpiresAt);
public sealed record PendingLinkRequest(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The signed-in account's family in this server, plus what it may do next.</summary>
public sealed record LinkedAccountsView(ulong Viewer, ulong PrimaryId, AccountLink[] Alternates,
    PendingLinkRequest[] Requests, bool ViewerIsPrimary, string? CannotCreateReason)
{
    public bool CanCreateRequest => CannotCreateReason is null;
}

public enum ScheduleChoice
{
    KeepPrimary,
    KeepAlternate
}

public sealed record WeeklyConflict(DayOfWeek Day, WeeklyAvailability Primary, WeeklyAvailability Alternate);
public sealed record WindowConflict(AvailabilityWindow Alternate, AvailabilityWindow[] Primary);

/// <summary>What linking would change. Conflicts must be resolved and organizer roles acknowledged to approve.</summary>
public sealed record LinkPreview(ulong PrimaryId, ulong AlternateId, DateTimeOffset ExpiresAt,
    string[] SharedGroups, string[] GroupsFromAlternate, string[] GroupsFromPrimary, string[] OrganizedByAlternate,
    string[] MergedInvitations, int WindowsMoved, int WeeklyDaysMoved, int DuplicatesMerged,
    WeeklyConflict[] WeeklyConflicts, WindowConflict[] WindowConflicts);

public sealed record LinkResolutions(IReadOnlyDictionary<DayOfWeek, ScheduleChoice> Weekly,
    IReadOnlyDictionary<Guid, ScheduleChoice> Windows, bool AcknowledgeOrganizer)
{
    public static LinkResolutions None { get; } = new(new Dictionary<DayOfWeek, ScheduleChoice>(), new Dictionary<Guid, ScheduleChoice>(), false);
}

public sealed record LinkResult(ulong PrimaryId, bool AlreadyLinked, bool RoleSyncFailed);

/// <summary>What unlinking would change for the alternate account.</summary>
public sealed record UnlinkPreview(ulong PrimaryId, ulong AlternateId, string[] AlternateKeeps, string[] AlternateLoses,
    string[] AlternateStillOrganizes, int WindowsRestored, int WeeklyDaysRestored);

public sealed record UnlinkResult(ulong AlternateId, bool RoleSyncFailed);

public sealed record CreateAccountLinkRequestCommand(ulong GuildId, ulong UserId) : ICommand<CreatedLinkRequest>;
public sealed record RevokeAccountLinkRequestCommand(ulong GuildId, ulong UserId, Guid RequestId) : ICommand;
public sealed record GetLinkedAccountsQuery(ulong GuildId, ulong UserId) : IQuery<LinkedAccountsView>;

/// <summary><paramref name="UserId"/> is the signed-in account that would become the alternate.</summary>
public sealed record PreviewAccountLinkQuery(ulong GuildId, ulong UserId, Guid RequestId, string Token) : IQuery<LinkPreview>;
public sealed record ApproveAccountLinkCommand(ulong GuildId, ulong UserId, Guid RequestId, string Token, LinkResolutions Resolutions)
    : ICommand<LinkResult>;
public sealed record PreviewAdminAccountLinkQuery(ulong GuildId, ulong AdministratorId, ulong PrimaryId, ulong AlternateId)
    : IQuery<LinkPreview>;
public sealed record AdminLinkAccountsCommand(ulong GuildId, ulong AdministratorId, ulong PrimaryId, ulong AlternateId,
    LinkResolutions Resolutions) : ICommand<LinkResult>;
public sealed record PreviewUnlinkQuery(ulong GuildId, ulong UserId, ulong AlternateId) : IQuery<UnlinkPreview>;
public sealed record UnlinkAccountCommand(ulong GuildId, ulong UserId, ulong AlternateId) : ICommand<UnlinkResult>;

/// <summary>
/// Linking alternate accounts (twinks). Ownership is proven by two Discord sign-ins: the primary creates a
/// short-lived single-use request, and the alternate must sign in and approve it. IDs, usernames or the link
/// alone never establish ownership. Links are scoped to one server and never change Discord permissions.
/// </summary>
internal sealed class AccountLinkHandlers(IAccountLinkStore links, IGuildGroupStore groups, IAvailabilityStore availability,
    IGuildMemberGateway members, GroupRoles roles, IClock clock) :
    ICommandHandler<CreateAccountLinkRequestCommand, CreatedLinkRequest>, ICommandHandler<RevokeAccountLinkRequestCommand>,
    IQueryHandler<GetLinkedAccountsQuery, LinkedAccountsView>, IQueryHandler<PreviewAccountLinkQuery, LinkPreview>,
    ICommandHandler<ApproveAccountLinkCommand, LinkResult>, IQueryHandler<PreviewUnlinkQuery, UnlinkPreview>,
    ICommandHandler<UnlinkAccountCommand, UnlinkResult>,
    IQueryHandler<PreviewAdminAccountLinkQuery, LinkPreview>, ICommandHandler<AdminLinkAccountsCommand, LinkResult>
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    internal const int MaxAlternates = 5;
    internal const int MaxOpenRequests = 3;
    internal const string Unavailable = "This link request has expired, was revoked or was already used. Ask for a new one.";

    public async Task<Result<CreatedLinkRequest>> Handle(CreateAccountLinkRequestCommand command, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(command.GuildId, command.UserId);
        if (view.CannotCreateReason is { } reason)
            return Result.Failure<CreatedLinkRequest>(new("Link.Rule", reason));
        if (!await members.IsMemberAsync(command.GuildId, command.UserId))
            return Result.Failure<CreatedLinkRequest>(new("Link.Rule", "You need to be a member of this server."));

        string token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.UtcNow;
        var request = await links.CreateRequestAsync(command.GuildId, command.UserId, Hash(token), now, now + Lifetime);
        return Result.Success(new CreatedLinkRequest(request.Id, token, request.ExpiresAt));
    }

    public async Task<Result> Handle(RevokeAccountLinkRequestCommand command, CancellationToken cancellationToken) =>
        await links.RevokeRequestAsync(command.GuildId, command.UserId, command.RequestId)
            ? Result.Success()
            : Result.Failure(new("Link.NotFound", "That request is no longer open."));

    public async Task<Result<LinkedAccountsView>> Handle(GetLinkedAccountsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await ViewAsync(query.GuildId, query.UserId));

    public async Task<Result<LinkPreview>> Handle(PreviewAccountLinkQuery query, CancellationToken cancellationToken)
    {
        var (request, map, error) = await ValidateAsync(query.GuildId, query.UserId, query.RequestId, query.Token);
        if (error is not null)
            return Result.Failure<LinkPreview>(new("Link.Rule", error));
        return Result.Success(await PlanAsync(query.GuildId, request!, query.UserId, map!));
    }

    public async Task<Result<LinkResult>> Handle(ApproveAccountLinkCommand command, CancellationToken cancellationToken) =>
        await roles.RunAsync(async () =>
        {
            var (request, map, error) = await ValidateAsync(command.GuildId, command.UserId, command.RequestId, command.Token,
                allowRetry: true);
            if (error is not null)
                return Result.Failure<LinkResult>(new("Link.Rule", error));
            if (request!.ConsumedAt is not null)
                return Result.Success(new LinkResult(request.PrimaryId, AlreadyLinked: true, RoleSyncFailed: false));

            return await ApplyLinkAsync(command.GuildId, request, command.UserId, map!, command.Resolutions);
        }, cancellationToken);

    public async Task<Result<LinkPreview>> Handle(PreviewAdminAccountLinkQuery query, CancellationToken cancellationToken)
    {
        var (map, error) = await ValidateAdminLinkAsync(query.GuildId, query.AdministratorId, query.PrimaryId, query.AlternateId);
        if (error is not null)
            return Result.Failure<LinkPreview>(new("Link.Rule", error));

        var now = clock.UtcNow;
        var request = new AccountLinkRequest(Guid.Empty, query.PrimaryId, [], now, now + Lifetime, null, null, false);
        return Result.Success(await PlanAsync(query.GuildId, request, query.AlternateId, map));
    }

    public Task<Result<LinkResult>> Handle(AdminLinkAccountsCommand command, CancellationToken cancellationToken) =>
        roles.RunAsync(async () =>
        {
            var (map, error) = await ValidateAdminLinkAsync(command.GuildId, command.AdministratorId, command.PrimaryId, command.AlternateId);
            if (error is not null)
                return Result.Failure<LinkResult>(new("Link.Rule", error));

            var now = clock.UtcNow;
            // This request has no owner-facing token and can only be consumed by the admin command.
            var request = await links.CreateRequestAsync(command.GuildId, command.PrimaryId,
                RandomNumberGenerator.GetBytes(32), now, now + Lifetime);
            try
            {
                return await ApplyLinkAsync(command.GuildId, request, command.AlternateId, map, command.Resolutions);
            }
            finally
            {
                await links.RevokeRequestAsync(command.GuildId, command.PrimaryId, request.Id);
            }
        }, cancellationToken);

    private async Task<(MemberLinks Map, string? Error)> ValidateAdminLinkAsync(ulong guildId, ulong administrator,
        ulong primary, ulong alternate)
    {
        if (!await members.IsAdministratorAsync(guildId, administrator))
            return (MemberLinks.None, "Only server administrators can link other accounts.");

        var map = await links.LoadAsync(guildId);
        if (primary == alternate)
            return (map, "Choose two different accounts.");
        if (map.Member(primary) != primary || map.IsLinked(alternate))
            return (map, "This account is already part of another linked family. Unlink it first.");
        if (map.Alternates(primary).Length >= MaxAlternates)
            return (map, $"An account can have up to {MaxAlternates} linked alternates.");
        if (!await members.IsMemberAsync(guildId, primary) || !await members.IsMemberAsync(guildId, alternate))
            return (map, "Both accounts must be members of this server.");
        return (map, null);
    }

    private async Task<Result<LinkResult>> ApplyLinkAsync(ulong guildId, AccountLinkRequest request, ulong alternate,
        MemberLinks map, LinkResolutions choices)
    {
        var preview = await PlanAsync(guildId, request, alternate, map);
        if (preview.WeeklyConflicts.Any(conflict => !choices.Weekly.ContainsKey(conflict.Day)) ||
            preview.WindowConflicts.Any(conflict => !choices.Windows.ContainsKey(conflict.Alternate.Id)))
            return Result.Failure<LinkResult>(new("Link.Conflict", "Choose which schedule to keep for each conflict."));
        if (preview.OrganizedByAlternate.Length > 0 && !choices.AcknowledgeOrganizer)
            return Result.Failure<LinkResult>(new("Link.Conflict", "Confirm that organizer rights will be shared."));

        var plan = await BuildPlanAsync(guildId, request, alternate, map, choices);
        switch (await links.LinkAsync(guildId, plan))
        {
            case LinkOutcome.AlreadyLinked:
                return Result.Success(new LinkResult(request.PrimaryId, true, false));
            case LinkOutcome.RequestUnavailable:
                return Result.Failure<LinkResult>(new("Link.Rule", Unavailable));
            case LinkOutcome.AccountUnavailable:
                return Result.Failure<LinkResult>(new("Link.Rule", "One of these accounts was linked elsewhere meanwhile. Unlink it first."));
        }

        bool failed = !await TrySyncAsync(guildId, [request.PrimaryId, alternate]);
        return Result.Success(new LinkResult(request.PrimaryId, false, failed));
    }

    public async Task<Result<UnlinkPreview>> Handle(PreviewUnlinkQuery query, CancellationToken cancellationToken)
    {
        var map = await links.LoadAsync(query.GuildId);
        if (Authorize(map, query.UserId, query.AlternateId) is { } error)
            return Result.Failure<UnlinkPreview>(new("Link.Rule", error));

        ulong primary = map.Member(query.AlternateId);
        var family = map.Accounts(primary).Where(id => id != query.AlternateId).ToArray();
        var saved = await groups.ListAsync(query.GuildId);
        var snapshot = await links.GetSnapshotAsync(query.GuildId, query.AlternateId) ?? AvailabilitySnapshot.Empty;
        var restore = Restorable(snapshot);
        return Result.Success(new UnlinkPreview(primary, query.AlternateId,
            saved.Where(group => group.Members.Contains(query.AlternateId)).Select(group => group.Name).ToArray(),
            saved.Where(group => !group.Members.Contains(query.AlternateId) && group.Members.Any(family.Contains))
                .Select(group => group.Name).ToArray(),
            saved.Where(group => group.OrganizerId == query.AlternateId).Select(group => group.Name).ToArray(),
            restore.Windows.Length, restore.Weekly.Length));
    }

    public async Task<Result<UnlinkResult>> Handle(UnlinkAccountCommand command, CancellationToken cancellationToken) =>
        await roles.RunAsync(async () =>
        {
            var map = await links.LoadAsync(command.GuildId);
            if (Authorize(map, command.UserId, command.AlternateId) is { } error)
                return Result.Failure<UnlinkResult>(new("Link.Rule", error));

            ulong primary = map.Member(command.AlternateId);
            var snapshot = await links.GetSnapshotAsync(command.GuildId, command.AlternateId) ?? AvailabilitySnapshot.Empty;
            if (!await links.UnlinkAsync(command.GuildId, primary, command.AlternateId, Restorable(snapshot)))
                return Result.Failure<UnlinkResult>(new("Link.Rule", "That account isn't linked."));

            // Roles the alternate held only through the link go; its own memberships keep theirs.
            bool failed = !await TrySyncAsync(command.GuildId, [command.AlternateId, primary]);
            return Result.Success(new UnlinkResult(command.AlternateId, failed));
        }, cancellationToken);

    private async Task<LinkedAccountsView> ViewAsync(ulong guildId, ulong userId)
    {
        var all = await links.ListAsync(guildId);
        var map = MemberLinks.From(all);
        ulong primary = map.Member(userId);
        var family = all.Where(link => link.PrimaryId == primary).ToArray();
        bool isPrimary = primary == userId;
        var now = clock.UtcNow;
        var requests = isPrimary
            ? (await links.ListRequestsAsync(guildId, userId)).Where(request => request.IsPending(now))
                .Select(request => new PendingLinkRequest(request.Id, request.CreatedAt, request.ExpiresAt)).ToArray()
            : [];
        string? reason = !isPrimary ? "This account is linked as an alternate. Create requests from your primary account."
            : family.Length >= MaxAlternates ? $"You can link up to {MaxAlternates} alternate accounts."
            : requests.Length >= MaxOpenRequests ? "You already have 3 open requests. Revoke one or wait for it to expire."
            : null;
        return new LinkedAccountsView(userId, primary, family, requests, isPrimary, reason);
    }

    /// <summary>
    /// Validates the request for the signed-in would-be alternate. Errors never reveal whether a request ID exists
    /// for someone else: an unknown ID, a wrong secret, another server and an expired request look the same.
    /// </summary>
    private async Task<(AccountLinkRequest? Request, MemberLinks? Map, string? Error)> ValidateAsync(ulong guildId, ulong alternate,
        Guid requestId, string token, bool allowRetry = false)
    {
        var request = await links.GetRequestAsync(guildId, requestId);
        if (request is null || !CryptographicOperations.FixedTimeEquals(request.TokenHash, Hash(token ?? "")))
            return (null, null, Unavailable);

        var map = await links.LoadAsync(guildId);
        if (request.ConsumedAt is not null)
            return allowRetry && request.ConsumedBy == alternate && map.Member(alternate) == request.PrimaryId
                ? (request, map, null)
                : (null, null, Unavailable);
        if (!request.IsPending(clock.UtcNow))
            return (null, null, Unavailable);
        if (alternate == request.PrimaryId)
            return (null, null, "You're signed in with the account that created this request. Sign in with your alternate account (twink) to approve it.");
        if (map.Member(request.PrimaryId) != request.PrimaryId)
            return (null, null, "The account that created this request is now linked as an alternate itself.");
        if (map.IsLinked(alternate))
            return (null, null, map.Member(alternate) == request.PrimaryId
                ? "These accounts are already linked."
                : "This account is already part of another linked family. Unlink it first.");
        if (map.Alternates(request.PrimaryId).Length >= MaxAlternates)
            return (null, null, $"An account can have up to {MaxAlternates} linked alternates.");
        if (!await members.IsMemberAsync(guildId, request.PrimaryId) || !await members.IsMemberAsync(guildId, alternate))
            return (null, null, "Both accounts must be members of this server.");
        return (request, map, null);
    }

    private static string? Authorize(MemberLinks map, ulong actor, ulong alternate)
    {
        ulong primary = map.Member(alternate);
        if (primary == alternate)
            return "That account isn't linked.";
        return actor == alternate || actor == primary ? null : "Only the primary account or that alternate can unlink it.";
    }

    private async Task<LinkPreview> PlanAsync(ulong guildId, AccountLinkRequest request, ulong alternate, MemberLinks map)
    {
        var (preview, _) = await AnalyzeAsync(guildId, request, alternate, map);
        return preview;
    }

    private async Task<LinkPlan> BuildPlanAsync(ulong guildId, AccountLinkRequest request, ulong alternate, MemberLinks map,
        LinkResolutions choices)
    {
        var (preview, data) = await AnalyzeAsync(guildId, request, alternate, map);
        var keepAlternate = preview.WindowConflicts.Where(conflict => choices.Windows[conflict.Alternate.Id] == ScheduleChoice.KeepAlternate).ToArray();
        var weeklyToSet = data.MovedWeekly
            .Concat(preview.WeeklyConflicts.Where(conflict => choices.Weekly[conflict.Day] == ScheduleChoice.KeepAlternate)
                .Select(conflict => conflict.Alternate))
            .Select(day => day with { })
            .ToArray();
        var family = map.Accounts(request.PrimaryId).Append(alternate).ToHashSet();
        return new LinkPlan(request.Id, request.PrimaryId, alternate, clock.UtcNow, data.Snapshot,
            data.MovedWindows.Concat(keepAlternate.Select(conflict => conflict.Alternate.Id)).ToArray(),
            keepAlternate.SelectMany(conflict => conflict.Primary.Select(window => window.Id)).Distinct().ToArray(),
            weeklyToSet,
            group => DeduplicateInvitations(group, family, request.PrimaryId));
    }

    private sealed record Analysis(AvailabilitySnapshot Snapshot, Guid[] MovedWindows, WeeklyAvailability[] MovedWeekly);

    private async Task<(LinkPreview Preview, Analysis Data)> AnalyzeAsync(ulong guildId, AccountLinkRequest request, ulong alternate,
        MemberLinks map)
    {
        ulong primary = request.PrimaryId;
        var primaryFamily = map.Accounts(primary);
        var saved = await groups.ListAsync(guildId);
        bool Primary(IEnumerable<ulong> ids) => ids.Any(primaryFamily.Contains);

        string[] Names(Func<GuildGroup, bool> predicate) => saved.Where(predicate).Select(group => group.Name).ToArray();
        var shared = Names(group => group.Members.Contains(alternate) && Primary(group.Members));
        var fromAlternate = Names(group => group.Members.Contains(alternate) && !Primary(group.Members));
        var fromPrimary = Names(group => Primary(group.Members) && !group.Members.Contains(alternate));
        var organized = Names(group => group.OrganizerId == alternate);
        var family = primaryFamily.Append(alternate).ToHashSet();
        var merged = Names(group => !ReferenceEquals(DeduplicateInvitations(group, family, primary), group));

        var now = clock.UtcNow;
        var altWindows = (await availability.ListAsync(guildId, alternate)).ToArray();
        var primaryWindows = (await availability.ListAsync(guildId, primary)).Where(window => window.End > now).ToArray();
        var altWeekly = (await availability.ListWeeklyAsync(guildId, alternate)).ToArray();
        var primaryWeekly = (await availability.ListWeeklyAsync(guildId, primary)).ToDictionary(day => day.Day);

        var moved = new List<Guid>();
        var conflicts = new List<WindowConflict>();
        int duplicates = 0;
        foreach (var window in altWindows.Where(window => window.End > now))
        {
            if (primaryWindows.Any(item => item.Start == window.Start && item.End == window.End))
            {
                duplicates++;
                continue;
            }
            var overlapping = primaryWindows.Where(item => item.Start < window.End && window.Start < item.End).ToArray();
            if (overlapping.Length > 0)
                conflicts.Add(new WindowConflict(window, overlapping));
            else
                moved.Add(window.Id);
        }

        var movedWeekly = new List<WeeklyAvailability>();
        var weeklyConflicts = new List<WeeklyConflict>();
        foreach (var day in altWeekly)
        {
            if (!primaryWeekly.TryGetValue(day.Day, out var existing))
                movedWeekly.Add(day);
            else if (existing == day)
                duplicates++;
            else
                weeklyConflicts.Add(new WeeklyConflict(day.Day, existing, day));
        }

        var preview = new LinkPreview(primary, alternate, request.ExpiresAt, shared, fromAlternate, fromPrimary, organized, merged,
            moved.Count, movedWeekly.Count, duplicates, weeklyConflicts.ToArray(), conflicts.ToArray());
        return (preview, new Analysis(new AvailabilitySnapshot(altWindows, altWeekly), moved.ToArray(), movedWeekly.ToArray()));
    }

    /// <summary>
    /// One invitation per member: drops invitations for family accounts when the family already belongs to the
    /// group, and keeps a single invitation (the primary's when present) when several family accounts were invited.
    /// Returns the same instance when nothing changes.
    /// </summary>
    internal static GuildGroup DeduplicateInvitations(GuildGroup group, IReadOnlySet<ulong> family, ulong primary)
    {
        var invited = group.Invites.Where(family.Contains).ToArray();
        if (invited.Length == 0)
            return group;

        ulong[] drop = group.Members.Any(family.Contains) ? invited
            : invited.Length > 1 ? invited.Where(id => id != (invited.Contains(primary) ? primary : invited[0])).ToArray()
            : [];
        if (drop.Length == 0)
            return group;

        return group with
        {
            Invites = group.Invites.Where(id => !drop.Contains(id)).ToArray(),
            InviteTokens = GroupInvitation.RemoveTokens(group, drop) is { Count: > 0 } left ? left : null
        };
    }

    /// <summary>What an alternate gets back on unlink: its saved weekly hours and pre-link windows that haven't ended.</summary>
    private AvailabilitySnapshot Restorable(AvailabilitySnapshot snapshot)
    {
        var now = clock.UtcNow;
        return new AvailabilitySnapshot(snapshot.Windows.Where(window => window.End > now).ToArray(), snapshot.Weekly);
    }

    private async Task<bool> TrySyncAsync(ulong guildId, IEnumerable<ulong> accounts)
    {
        try
        {
            foreach (ulong account in accounts.Distinct())
                await roles.SyncAsync(guildId, account);
            return true;
        }
        catch (GroupRoleException)
        {
            return false;
        }
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
