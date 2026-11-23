using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Players;

/// <summary>A player's own page: their main Steam account, their games and whether library imports are set up.</summary>
public sealed record MyGames(SteamAccount? Steam, IReadOnlyList<PlayerGame> Games, bool CanImport)
{
    /// <summary>Every linked Steam account, main first; the others are twinks.</summary>
    public IReadOnlyList<SteamAccount> Accounts { get; init; } = [];
}

/// <summary>Limits for twink accounts.</summary>
public static class SteamAccounts
{
    public const int Max = 5;
    public const int LabelLength = 32;
}

public sealed record GetMyGamesQuery(ulong UserId) : IQuery<MyGames>;

/// <summary>
/// Links a pasted SteamID64, profile link or custom URL name. The link is unverified. With <paramref name="Twink"/> the
/// account is added next to the others; otherwise it replaces the main account.
/// </summary>
public sealed record LinkSteamCommand(ulong UserId, string Input, bool Twink = false) : ICommand<SteamAccount>;

/// <summary>
/// Links the Steam account proven by a Steam sign-in response (the <c>openid.*</c> values Steam sent back to
/// <paramref name="ReturnTo"/>). It replaces an unverified claim on the same account by someone else.
/// </summary>
public sealed record VerifySteamCommand(ulong UserId, IReadOnlyDictionary<string, string> Response, string ReturnTo, bool Twink = false) : ICommand<SteamAccount>;

/// <summary>
/// Links the Steam account connected to the person's Discord profile (read at dashboard sign-in). Discord verifies
/// ownership when someone connects Steam, so a connection Discord marks verified counts as verified here too.
/// </summary>
public sealed record LinkDiscordSteamCommand(ulong UserId, string SteamId, bool DiscordVerified) : ICommand<SteamAccount>;

/// <summary>
/// Removes one Steam account (<paramref name="SteamId"/>), or every link and every game imported from them when it is null
/// or the last account. Games added by hand stay.
/// </summary>
public sealed record UnlinkSteamCommand(ulong UserId, string? SteamId = null) : ICommand;

/// <summary>Makes one of the person's Steam accounts the main one.</summary>
public sealed record SetMainSteamCommand(ulong UserId, string SteamId) : ICommand;

/// <summary>Names one of the person's Steam accounts ("Smurf"); an empty label removes the name.</summary>
public sealed record SetSteamLabelCommand(ulong UserId, string SteamId, string? Label) : ICommand;

/// <summary>
/// Imports the public libraries of every linked Steam account together: a game owned on several accounts is one game with
/// the playtimes added up. New games start as "Can play"; marks already set are kept.
/// </summary>
public sealed record ImportSteamGamesCommand(ulong UserId) : ICommand<SteamImport>;

/// <param name="Skipped">Linked accounts whose library couldn't be read (private or Steam didn't answer).</param>
public sealed record SteamImport(int Added, int Updated, int Total, int Skipped = 0);

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
      ICommandHandler<LinkDiscordSteamCommand, SteamAccount>, ICommandHandler<UnlinkSteamCommand>, ICommandHandler<ImportSteamGamesCommand, SteamImport>,
      ICommandHandler<SetMainSteamCommand>, ICommandHandler<SetSteamLabelCommand>, ICommandHandler<AddPlayerGameCommand, PlayerGame>,
      ICommandHandler<SetGamePreferenceCommand, PlayerGame>, ICommandHandler<SetGamePreferencesCommand, int>, ICommandHandler<RemovePlayerGameCommand>,
      IQueryHandler<GetGroupGameInterestQuery, GroupGameInterest>
{
    private const int MaxGames = 2000;
    private static ErrorDescriptor Error(string code, string text) => new("Players." + code, text);

    public async Task<Result<MyGames>> Handle(GetMyGamesQuery query, CancellationToken cancellationToken)
    {
        var accounts = await store.ListSteamAsync(query.UserId);
        return new MyGames(accounts.Count > 0 ? accounts[0] : null, await store.ListAsync(query.UserId), steam.CanImport) { Accounts = accounts };
    }

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
        return await AddAsync(command.UserId, id, false, command.Twink);
    }

    /// <summary>
    /// Adds the account: as a twink next to the others, or as the new main account in place of the old main one. Linking
    /// an account that is already linked only upgrades it to verified.
    /// </summary>
    private async Task<Result<SteamAccount>> AddAsync(ulong userId, string steamId, bool verified, bool twink)
    {
        var accounts = await store.ListSteamAsync(userId);
        if (accounts.FirstOrDefault(account => account.SteamId == steamId) is { } known)
        {
            var upgraded = known with { Verified = known.Verified || verified };
            await store.SaveSteamAsync(upgraded);
            return upgraded;
        }
        if (twink && accounts.Count > 0)
        {
            if (accounts.Count >= SteamAccounts.Max)
                return Result.Failure<SteamAccount>(Error("SteamTooMany", $"You can link up to {SteamAccounts.Max} Steam accounts. Remove one first."));
            var extra = new SteamAccount(userId, steamId, verified, clock.UtcNow, null) { IsMain = false };
            await store.SaveSteamAsync(extra);
            return extra;
        }
        // A new main account: alone it replaces everything (its games came from the old account), next to twinks only the old main goes.
        if (accounts.FirstOrDefault(account => account.IsMain) is { } main)
        {
            if (accounts.Count == 1)
                await store.RemoveSteamAsync(userId);
            else
                await store.RemoveSteamAccountAsync(userId, main.SteamId);
        }
        var account = new SteamAccount(userId, steamId, verified, clock.UtcNow, null) { IsMain = true };
        await store.SaveSteamAsync(account);
        await store.SetMainSteamAsync(userId, steamId);
        return account;
    }

    public async Task<Result> Handle(SetMainSteamCommand command, CancellationToken cancellationToken)
    {
        if ((await store.ListSteamAsync(command.UserId)).All(account => account.SteamId != command.SteamId))
            return Result.Failure(Error("SteamNotLinked", "Link your Steam account first."));
        await store.SetMainSteamAsync(command.UserId, command.SteamId);
        return Result.Success();
    }

    public async Task<Result> Handle(SetSteamLabelCommand command, CancellationToken cancellationToken)
    {
        if ((await store.ListSteamAsync(command.UserId)).FirstOrDefault(account => account.SteamId == command.SteamId) is not { } account)
            return Result.Failure(Error("SteamNotLinked", "Link your Steam account first."));
        string label = string.Join(' ', (command.Label ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (label.Length > SteamAccounts.LabelLength || label.Any(char.IsControl))
            return Result.Failure(Error("SteamLabel", $"Use up to {SteamAccounts.LabelLength} characters for the account name."));
        await store.SaveSteamAsync(account with { Label = label.Length == 0 ? null : label });
        return Result.Success();
    }

    public async Task<Result<SteamAccount>> Handle(VerifySteamCommand command, CancellationToken cancellationToken)
    {
        string? steamId = await steam.VerifySignInAsync(command.Response, command.ReturnTo, cancellationToken);
        return SteamIds.IsSteamId64(steamId)
            ? await LinkProvenAsync(command.UserId, steamId!, true, command.Twink)
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
    private async Task<Result<SteamAccount>> LinkProvenAsync(ulong userId, string steamId, bool verified, bool twink = false)
    {
        if (await store.FindSteamOwnerAsync(steamId) is { } owner && owner.UserId != userId)
        {
            if (owner.Verified)
                return Result.Failure<SteamAccount>(Error("SteamVerifiedElsewhere", "That Steam account is already verified by another Discord account."));
            // Only the unverified claim on this account gives way; the other person's other accounts stay.
            if ((await store.ListSteamAsync(owner.UserId)).Count == 1)
                await store.RemoveSteamAsync(owner.UserId);
            else
                await store.RemoveSteamAccountAsync(owner.UserId, steamId);
        }
        return await AddAsync(userId, steamId, verified, twink);
    }

    public async Task<Result> Handle(UnlinkSteamCommand command, CancellationToken cancellationToken)
    {
        var accounts = await store.ListSteamAsync(command.UserId);
        if (command.SteamId is { } one && accounts.Count > 1 && accounts.Any(account => account.SteamId == one))
            await store.RemoveSteamAccountAsync(command.UserId, one);
        else
            await store.RemoveSteamAsync(command.UserId);
        return Result.Success();
    }

    public async Task<Result<SteamImport>> Handle(ImportSteamGamesCommand command, CancellationToken cancellationToken)
    {
        var accounts = await store.ListSteamAsync(command.UserId);
        if (accounts.Count == 0)
            return Result.Failure<SteamImport>(Error("SteamNotLinked", "Link your Steam account first."));
        // Every account's library, merged: a game owned on several accounts counts once, with the playtimes added up.
        var merged = new Dictionary<string, SteamOwnedGame>(StringComparer.Ordinal);
        var imported = new List<SteamAccount>();
        SteamLibrary? problem = null;
        foreach (var account in accounts)
        {
            var owned = await steam.GetOwnedGamesAsync(account.SteamId, cancellationToken);
            if (owned.Status != SteamLibraryStatus.Ok)
            {
                problem ??= owned;
                continue;
            }
            imported.Add(account);
            foreach (var game in owned.Games)
                merged[game.AppId] = merged.TryGetValue(game.AppId, out var seen)
                    ? seen with { PlaytimeMinutes = seen.PlaytimeMinutes + game.PlaytimeMinutes, RecentMinutes = seen.RecentMinutes + game.RecentMinutes }
                    : game;
        }
        var library = imported.Count > 0 ? new SteamLibrary(SteamLibraryStatus.Ok, merged.Values.ToArray()) : problem!;
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
            var match = existing.GetValueOrDefault(key) ?? existing.Values.FirstOrDefault(game => game.Provider is not "steam" && GameKeys.Same(game, owned.Name, null, null));
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
        foreach (var account in imported)
            await store.SaveSteamAsync(account with { ImportedAt = now });
        return new SteamImport(added, updated, library.Games.Count, accounts.Count - imported.Count);
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
