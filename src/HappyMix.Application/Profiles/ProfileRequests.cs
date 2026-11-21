using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Profiles;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Profiles;

/// <summary>Saves what Discord just said about the person; <see cref="UserProfile.SyncedAt"/> is set to now.</summary>
public sealed record SaveDiscordProfileCommand(ulong UserId, string UserName, string? DisplayName, string? Avatar, DiscordSteamConnection? Steam,
    bool ConnectionsChecked) : ICommand<UserProfile>;

public sealed record GetUserProfileQuery(ulong UserId) : IQuery<UserProfile?>;

internal sealed class ProfileHandlers(IUserProfileStore store, IClock clock) :
    ICommandHandler<SaveDiscordProfileCommand, UserProfile>, IQueryHandler<GetUserProfileQuery, UserProfile?>
{
    public async Task<Result<UserProfile>> Handle(SaveDiscordProfileCommand command, CancellationToken cancellationToken)
    {
        if (command.UserId == 0 || string.IsNullOrWhiteSpace(command.UserName))
            return Result.Failure<UserProfile>(new ErrorDescriptor("Profile.Invalid", "Discord didn't send a user name."));
        var saved = await store.GetAsync(command.UserId);
        var profile = new UserProfile(command.UserId, Trim(command.UserName, 64)!, Trim(command.DisplayName, 64), Trim(command.Avatar, 80),
            // When Discord didn't answer the connections request, keep what the last sync knew instead of forgetting Steam.
            command.ConnectionsChecked ? command.Steam : saved?.Steam, command.ConnectionsChecked || saved?.ConnectionsChecked == true, clock.UtcNow);
        await store.SaveAsync(profile);
        return profile;
    }

    public async Task<Result<UserProfile?>> Handle(GetUserProfileQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.GetAsync(query.UserId));

    private static string? Trim(string? value, int max) =>
        value?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(max, trimmed.Length)] : null;
}
