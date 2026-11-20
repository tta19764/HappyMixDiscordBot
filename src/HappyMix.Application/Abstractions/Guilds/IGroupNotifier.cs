namespace HappyMix.Application.Abstractions.Guilds;

public enum GroupNoticeKind
{
    /// <summary>To the organizer: someone asked to join.</summary>
    JoinRequested,
    /// <summary>To the requester: the organizer let them in.</summary>
    JoinApproved,
    /// <summary>To the requester: the organizer said no.</summary>
    JoinDeclined,
    /// <summary>To the organizer: a member suggested a game.</summary>
    GameSuggested,
    /// <summary>To the member who suggested it: the game was added.</summary>
    SuggestionAccepted
}

/// <summary>A short message about a group for one person, e.g. a direct message.</summary>
/// <param name="SuggestionId">For <see cref="GroupNoticeKind.GameSuggested"/>: the suggestion, so the message can offer Add and Dismiss.</param>
public sealed record GroupNotice(GroupNoticeKind Kind, ulong GuildId, Guid GroupId, string GroupName, ulong RecipientId, ulong ActorId,
    string? Detail = null, Guid? SuggestionId = null);

/// <summary>Delivers group notices. Delivery is best effort: a closed inbox never fails the action.</summary>
public interface IGroupNotifier
{
    Task NotifyAsync(GroupNotice notice, CancellationToken cancellationToken);
}
