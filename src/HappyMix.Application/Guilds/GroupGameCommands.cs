using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>Attached-game use cases. They share the group command gate and management rules.</summary>
internal sealed partial class GroupCommands
{
    private const int MaxGames = 20;
    private const string TooManyGames = "A group can have up to 20 games.";
    private const string DuplicateGame = "This group already has that Steam game.";
    private const string MissingGame = "That game is no longer attached to this group.";

    public Task<Result<GroupChange>> Handle(AddGroupGameCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            var game = NormalizeGame(command.Name, command.Steam);
            var existing = (await groups.ListGamesAsync(command.Actor.GuildId)).Where(item => item.GroupId == group.Id).ToArray();
            if (existing.Length >= MaxGames)
                throw new GroupRuleException(TooManyGames);
            if (!await groups.SaveGameAsync(command.Actor.GuildId, CreateGame(group.Id, Guid.NewGuid(), game.Name, game.SteamAppId)))
                throw new GroupRuleException(DuplicateGame);
            return new GroupChange(group.Name, GroupId: group.Id);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(UpdateGroupGameCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            var game = NormalizeGame(command.Name, command.Steam);
            if (!(await groups.ListGamesAsync(command.Actor.GuildId)).Any(item => item.GroupId == group.Id && item.Id == command.GameId))
                throw new GroupRuleException(MissingGame);
            if (!await groups.SaveGameAsync(command.Actor.GuildId, CreateGame(group.Id, command.GameId, game.Name, game.SteamAppId)))
                throw new GroupRuleException(DuplicateGame);
            return new GroupChange(group.Name, GroupId: group.Id);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(RemoveGroupGameCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            if (!await groups.RemoveGameAsync(command.Actor.GuildId, group.Id, command.GameId))
                throw new GroupRuleException(MissingGame);
            return new GroupChange(group.Name, GroupId: group.Id);
        }, cancellationToken);

    private async Task<GuildGroup> RequireManagedByIdAsync(GroupActor actor, Guid groupId)
    {
        var group = (await groups.ListAsync(actor.GuildId)).FirstOrDefault(item => item.Id == groupId);
        // Hide groups the actor cannot see so a guessed ID reveals nothing.
        if (group is null || !GroupWorkspaceQueries.CanSee(group, actor))
            throw new GroupRuleException(GroupWorkspaceQueries.NotFound);
        return RequireManaged(group, actor);
    }

    private static (string Name, string? SteamAppId) NormalizeGame(string name, string? steam)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
            throw new GroupRuleException("Enter a game name of 1 to 60 characters.");
        if (!SteamApps.TryNormalize(steam, out string? appId))
            throw new GroupRuleException(SteamApps.InvalidMessage);
        return (name, appId);
    }

    private static GroupGame CreateGame(Guid groupId, Guid gameId, string name, string? steamAppId) =>
        new(gameId, groupId, name, steamAppId is null ? null : GroupGame.SteamProvider, steamAppId);
}
