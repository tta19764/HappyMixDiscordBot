using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Players;

/// <summary>A player's own page: their Steam link, their games and whether library imports are set up.</summary>
public sealed record MyGames(SteamAccount? Steam, IReadOnlyList<PlayerGame> Games, bool CanImport);

public sealed record GetMyGamesQuery(ulong UserId) : IQuery<MyGames>;

/// <summary>Links a pasted SteamID64, profile link or custom URL name. The link is unverified.</summary>
public sealed record LinkSteamCommand(ulong UserId, string Input) : ICommand<SteamAccount>;

/// <summary>
/// Links the Steam account proven by a Steam sign-in response (the <c>openid.*</c> values Steam sent back to
/// <paramref name="ReturnTo"/>). It replaces an unverified claim on the same account by someone else.
/// </summary>
public sealed record VerifySteamCommand(ulong UserId, IReadOnlyDictionary<string, string> Response, string ReturnTo) : ICommand<SteamAccount>;

/// <summary>
/// Links the Steam account connected to the person's Discord profile (read at dashboard sign-in). Discord verifies
/// ownership when someone connects Steam, so a connection Discord marks verified counts as verified here too.
/// </summary>
public sealed record LinkDiscordSteamCommand(ulong UserId, string SteamId, bool DiscordVerified) : ICommand<SteamAccount>;

/// <summary>Removes the Steam link and every game imported from it; games added by hand stay.</summary>
public sealed record UnlinkSteamCommand(ulong UserId) : ICommand;

/// <summary>Imports the linked account's public library. New games start as "Can play"; marks already set are kept.</summary>
public sealed record ImportSteamGamesCommand(ulong UserId) : ICommand<SteamImport>;

public sealed record SteamImport(int Added, int Updated, int Total);

/// <summary>
/// Adds a game by hand. <paramref name="Provider"/> and <paramref name="ExternalId"/> come from a catalog search (steam or igdb);
/// <paramref name="Steam"/> is an optional Steam App ID or store link.
/// </summary>
public sealed record AddPlayerGameCommand(ulong UserId, string Name, string? Steam, string? Provider, string? ExternalId,
    GamePreference Preference) : ICommand<PlayerGame>;

public sealed record SetGamePreferenceCommand(ulong UserId, string Key, GamePreference Preference) : ICommand<PlayerGame>;

public sealed record RemovePlayerGameCommand(ulong UserId, string Key) : ICommand;

/// <summary>
/// Saves several marks at once (the My games Save button). Games no longer on the list and unchanged marks are skipped;
/// the result is how many marks changed.
/// </summary>
public sealed record SetGamePreferencesCommand(ulong UserId, IReadOnlyDictionary<string, GamePreference> Marks) : ICommand<int>;

/// <summary>How the members of a group feel about a game: who wants it, who can play it and who won't.</summary>
public sealed record GameInterest(string Name, string? Provider, string? ExternalId, IReadOnlyList<ulong> Want,
    IReadOnlyList<ulong> Can, IReadOnlyList<ulong> Wont)
{
    public bool Any => Want.Count + Can.Count + Wont.Count > 0;
}

/// <param name="Games">Interest per group game, by game id.</param>
/// <param name="Popular">Games at least two members want that the group doesn't have yet, most wanted first.</param>
public sealed record GroupGameInterest(IReadOnlyDictionary<Guid, GameInterest> Games, IReadOnlyList<GameInterest> Popular);

public sealed record GetGroupGameInterestQuery(GroupActor Actor, Guid GroupId) : IQuery<GroupGameInterest>;

/// <summary>Identifies a game across Steam, IGDB and names typed by hand.</summary>
public static class GameKeys
{
    public static string For(string? provider, string? externalId, string name) =>
        provider is "steam" or "igdb" && externalId is { Length: > 0 } id ? $"{provider}:{id}" : "name:" + Normalize(name);

    public static string Normalize(string name) => string.Join(' ', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Same game: the same Steam app, the same IGDB entry, or the same name.</summary>
    public static bool Same(PlayerGame game, string name, string? provider, string? externalId) =>
        provider is "steam" or "igdb" && externalId is not null && game.Provider == provider && game.ExternalId == externalId
        || Normalize(game.Name) == Normalize(name);
}

internal sealed class PlayerGameHandlers(IPlayerGameStore store, ISteamDirectory steam, IClock clock, ISender sender, IAccountLinkStore accountLinks)
    : IQueryHandler<GetMyGamesQuery, MyGames>, ICommandHandler<LinkSteamCommand, SteamAccount>, ICommandHandler<VerifySteamCommand, SteamAccount>,
      ICommandHandler<LinkDiscordSteamCommand, SteamAccount>, ICommandHandler<UnlinkSteamCommand>, ICommandHandler<ImportSteamGamesCommand, SteamImport>, ICommandHandler<AddPlayerGameCommand, PlayerGame>,
      ICommandHandler<SetGamePreferenceCommand, PlayerGame>, ICommandHandler<SetGamePreferencesCommand, int>, ICommandHandler<RemovePlayerGameCommand>,
      IQueryHandler<GetGroupGameInterestQuery, GroupGameInterest>
{
    private const int MaxGames = 2000;
    private static ErrorDescriptor Error(string code, string text) => new("Players." + code, text);

    public async Task<Result<MyGames>> Handle(GetMyGamesQuery query, CancellationToken cancellationToken) =>
        new MyGames(await store.GetSteamAsync(query.UserId), await store.ListAsync(query.UserId), steam.CanImport);

    public async Task<Result<SteamAccount>> Handle(LinkSteamCommand command, CancellationToken cancellationToken)
    {
        string input = (command.Input ?? "").Trim();
        if (input.Length is 0 or > 200)
            return Result.Failure<SteamAccount>(Error("SteamInput", "Paste your SteamID64 or Steam profile link."));
        string? id = await steam.ResolveAsync(input, cancellationToken);
        if (id is null)
            return Result.Failure<SteamAccount>(Error("SteamNotFound", "That Steam profile wasn’t found. Paste your SteamID64 or the link to your profile."));
        if (await store.FindSteamOwnerAsync(id) is { } owner && owner.UserId != command.UserId)
            return Result.Failure<SteamAccount>(Error("SteamTaken", "Someone else has linked that Steam account. If it’s yours, verify it with Steam sign-in."));
        var current = await store.GetSteamAsync(command.UserId);
        if (current?.SteamId == id)
            return current;
        if (current is not null)
            await store.RemoveSteamAsync(command.UserId);
        var account = new SteamAccount(command.UserId, id, false, clock.UtcNow, null);
        await store.SaveSteamAsync(account);
        return account;
    }

    public async Task<Result<SteamAccount>> Handle(VerifySteamCommand command, CancellationToken cancellationToken)
    {
        string? steamId = await steam.VerifySignInAsync(command.Response, command.ReturnTo, cancellationToken);
        return SteamIds.IsSteamId64(steamId)
            ? await LinkProvenAsync(command.UserId, steamId!, true)
            : Result.Failure<SteamAccount>(Error("SteamSignInFailed", "Steam didn’t confirm the sign-in. Try again."));
    }

    public async Task<Result<SteamAccount>> Handle(LinkDiscordSteamCommand command, CancellationToken cancellationToken)
    {
        if (!SteamIds.IsSteamId64(command.SteamId))
            return Result.Failure<SteamAccount>(Error("SteamNotFound", "That Steam profile wasn’t found. Paste your SteamID64 or the link to your profile."));
        if (!command.DiscordVerified && await store.FindSteamOwnerAsync(command.SteamId) is { } owner && owner.UserId != command.UserId)
            return Result.Failure<SteamAccount>(Error("SteamTaken", "Someone else has linked that Steam account. If it’s yours, verify it with Steam sign-in."));
        return await LinkProvenAsync(command.UserId, command.SteamId, command.DiscordVerified);
    }

    /// <summary>Links an account whose ownership is proven: an unverified claim on it by someone else gives way.</summary>
    private async Task<Result<SteamAccount>> LinkProvenAsync(ulong userId, string steamId, bool verified)
    {
        if (await store.FindSteamOwnerAsync(steamId) is { } owner && owner.UserId != userId)
        {
            if (owner.Verified)
                return Result.Failure<SteamAccount>(Error("SteamVerifiedElsewhere", "That Steam account is already verified by another Discord account."));
            await store.RemoveSteamAsync(owner.UserId);
        }
        var current = await store.GetSteamAsync(userId);
        if (current is not null && current.SteamId != steamId)
            await store.RemoveSteamAsync(userId);
        var account = current is not null && current.SteamId == steamId
            ? current with { Verified = current.Verified || verified }
            : new SteamAccount(userId, steamId, verified, clock.UtcNow, null);
        await store.SaveSteamAsync(account);
        return account;
    }

    public async Task<Result> Handle(UnlinkSteamCommand command, CancellationToken cancellationToken)
    {
        await store.RemoveSteamAsync(command.UserId);
        return Result.Success();
    }

    public async Task<Result<SteamImport>> Handle(ImportSteamGamesCommand command, CancellationToken cancellationToken)
    {
        if (await store.GetSteamAsync(command.UserId) is not { } account)
            return Result.Failure<SteamImport>(Error("SteamNotLinked", "Link your Steam account first."));
        var library = await steam.GetOwnedGamesAsync(account.SteamId, cancellationToken);
        switch (library.Status)
        {
            case SteamLibraryStatus.NotConfigured:
                return Result.Failure<SteamImport>(Error("SteamImportOff", "Steam library import isn’t set up on this bot yet. Add games by hand for now."));
            case SteamLibraryStatus.Private:
                return Result.Failure<SteamImport>(Error("SteamPrivate", "Steam didn’t share your games: set Game details to Public in your Steam privacy settings, import, then switch it back."));
            case SteamLibraryStatus.Unavailable:
                return Result.Failure<SteamImport>(Error("SteamUnavailable", "Steam didn’t answer. Try again in a minute."));
        }

        var now = clock.UtcNow;
        var existing = (await store.ListAsync(command.UserId)).ToDictionary(game => game.Key, StringComparer.Ordinal);
        var changes = new List<PlayerGame>();
        int added = 0, updated = 0;
        foreach (var owned in library.Games.OrderByDescending(game => game.RecentMinutes).ThenByDescending(game => game.PlaytimeMinutes))
        {
            string key = GameKeys.For("steam", owned.AppId, owned.Name);
            var match = existing.GetValueOrDefault(key) ?? existing.Values.FirstOrDefault(game => game.Provider is null && GameKeys.Same(game, owned.Name, null, null));
            if (match is not null)
            {
                // Keep the player's mark; refresh the name, the Steam reference and the playtimes.
                changes.Add(match with { Name = owned.Name, Provider = "steam", ExternalId = owned.AppId, PlaytimeMinutes = owned.PlaytimeMinutes,
                    RecentMinutes = owned.RecentMinutes, UpdatedAt = now });
                if (match.Key != key)
                    await store.RemoveAsync(command.UserId, match.Key);
                updated++;
            }
            else if (existing.Count + added < MaxGames)
            {
                changes.Add(new PlayerGame(command.UserId, key, owned.Name, "steam", owned.AppId, GamePreference.Can, true,
                    owned.PlaytimeMinutes, owned.RecentMinutes, now));
                added++;
            }
        }
        await store.SaveManyAsync(changes.Select(game => game with { Key = GameKeys.For(game.Provider, game.ExternalId, game.Name) }).ToArray());
        await store.SaveSteamAsync(account with { ImportedAt = now });
        return new SteamImport(added, updated, library.Games.Count);
    }

    public async Task<Result<PlayerGame>> Handle(AddPlayerGameCommand command, CancellationToken cancellationToken)
    {
        string name = (command.Name ?? "").Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
            return Result.Failure<PlayerGame>(Error("GameName", "Enter a game name of 1 to 80 characters."));
        if (!Enum.IsDefined(command.Preference))
            return Result.Failure<PlayerGame>(Error("Preference", "Choose Want to play, Can play or Won’t play."));
        string? provider = null, externalId = null;
        if (command.Provider is "steam" or "igdb" && command.ExternalId is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit))
            (provider, externalId) = (command.Provider, id);
        else if (!string.IsNullOrWhiteSpace(command.Steam))
        {
            if (!SteamApps.TryNormalize(command.Steam, out string? appId))
                return Result.Failure<PlayerGame>(Error("SteamApp", SteamApps.InvalidMessage));
            (provider, externalId) = ("steam", appId);
        }

        var games = await store.ListAsync(command.UserId);
        if (games.FirstOrDefault(game => GameKeys.Same(game, name, provider, externalId)) is { } known)
        {
            var marked = known with { Preference = command.Preference, UpdatedAt = clock.UtcNow };
            await store.SaveAsync(marked);
            return marked;
        }
        if (games.Count >= MaxGames)
            return Result.Failure<PlayerGame>(Error("TooMany", "Your list is full. Remove games you no longer play first."));
        var game = new PlayerGame(command.UserId, GameKeys.For(provider, externalId, name), name, provider, externalId, command.Preference,
            false, null, null, clock.UtcNow);
        await store.SaveAsync(game);
        return game;
    }

    public async Task<Result<PlayerGame>> Handle(SetGamePreferenceCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(command.Preference))
            return Result.Failure<PlayerGame>(Error("Preference", "Choose Want to play, Can play or Won’t play."));
        if ((await store.ListAsync(command.UserId)).FirstOrDefault(game => game.Key == command.Key) is not { } game)
            return Result.Failure<PlayerGame>(Error("GameMissing", "That game isn’t on your list anymore."));
        var marked = game with { Preference = command.Preference, UpdatedAt = clock.UtcNow };
        await store.SaveAsync(marked);
        return marked;
    }

    public async Task<Result<int>> Handle(SetGamePreferencesCommand command, CancellationToken cancellationToken)
    {
        if (command.Marks.Values.Any(preference => !Enum.IsDefined(preference)))
            return Result.Failure<int>(Error("Preference", "Choose Want to play, Can play or Won’t play."));
        var now = clock.UtcNow;
        var changed = (await store.ListAsync(command.UserId))
            .Where(game => command.Marks.TryGetValue(game.Key, out var preference) && preference != game.Preference)
            .Select(game => game with { Preference = command.Marks[game.Key], UpdatedAt = now })
            .ToArray();
        if (changed.Length > 0)
            await store.SaveManyAsync(changed);
        return changed.Length;
    }

    public async Task<Result> Handle(RemovePlayerGameCommand command, CancellationToken cancellationToken) =>
        await store.RemoveAsync(command.UserId, command.Key) ? Result.Success() : Result.Failure(Error("GameMissing", "That game isn’t on your list anymore."));

    public async Task<Result<GroupGameInterest>> Handle(GetGroupGameInterestQuery query, CancellationToken cancellationToken)
    {
        var detail = await sender.Send(new GetGroupDetailQuery(query.Actor, query.GroupId), cancellationToken);
        if (detail.IsFailure || !detail.Value.CanViewRoster)
            return Result.Failure<GroupGameInterest>(Error("GroupMissing", "That group doesn’t exist."));
        var group = detail.Value;
        var links = await accountLinks.LoadAsync(query.Actor.GuildId);

        // Each member counts once, with the marks of any of their linked accounts (their own account first).
        var members = group.Members.Append(group.Summary.OrganizerId).Select(links.Member).Distinct().ToArray();
        var accounts = members.ToDictionary(member => member, member => links.Accounts(member).OrderBy(id => id == member ? 0 : 1).ToArray());
        var all = await store.ListForAsync(accounts.Values.SelectMany(ids => ids).Distinct().ToArray());
        var byAccount = all.GroupBy(game => game.UserId).ToDictionary(group => group.Key, group => group.ToArray());
        PlayerGame? MarkOf(ulong member, string name, string? provider, string? externalId) => accounts[member]
            .Select(account => byAccount.GetValueOrDefault(account)?.FirstOrDefault(game => GameKeys.Same(game, name, provider, externalId)))
            .FirstOrDefault(game => game is not null);
        GameInterest Interest(string name, string? provider, string? externalId)
        {
            var marks = members.Select(member => (Member: member, Game: MarkOf(member, name, provider, externalId))).Where(item => item.Game is not null).ToArray();
            ulong[] With(GamePreference preference) => marks.Where(item => item.Game!.Preference == preference).Select(item => item.Member).ToArray();
            return new GameInterest(name, provider, externalId, With(GamePreference.Want), With(GamePreference.Can), With(GamePreference.Wont));
        }

        var games = group.Games.ToDictionary(game => game.Id, game => Interest(game.Name, game.Provider, game.ExternalId));
        var popular = all.Where(game => game.Preference == GamePreference.Want)
            .Where(game => !group.Games.Any(known => GameKeys.Same(game, known.Name, known.Provider, known.ExternalId)))
            .GroupBy(game => game.Provider is not null ? game.Provider + ":" + game.ExternalId : GameKeys.Normalize(game.Name))
            .Select(candidates => candidates.OrderBy(game => game.Provider is null ? 1 : 0).First())
            .Select(game => Interest(game.Name, game.Provider, game.ExternalId))
            .Where(interest => interest.Want.Count >= 2)
            .OrderByDescending(interest => interest.Want.Count).ThenBy(interest => interest.Wont.Count).ThenBy(interest => interest.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8).ToArray();
        return new GroupGameInterest(games, popular);
    }
}

/// <summary>SteamID64 checks shared by the Steam directory and the commands.</summary>
public static class SteamIds
{
    public const string OpenIdEndpoint = "https://steamcommunity.com/openid/login";

    /// <summary>The Steam sign-in address (OpenID 2.0) that sends people back to <paramref name="returnTo"/>.</summary>
    public static string SignInUrl(string returnTo, string realm) =>
        OpenIdEndpoint + "?" + string.Join('&', new Dictionary<string, string>
        {
            ["openid.ns"] = "http://specs.openid.net/auth/2.0",
            ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = returnTo,
            ["openid.realm"] = realm,
            ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select"
        }.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));

    /// <summary>Individual accounts: 17 digits starting 7656119.</summary>
    public static bool IsSteamId64(string? value) =>
        value is { Length: 17 } && value.StartsWith("7656119", StringComparison.Ordinal) && value.All(char.IsAsciiDigit)
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
