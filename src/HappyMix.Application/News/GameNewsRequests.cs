using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.News;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.News;

/// <summary>The server's game news subscriptions, by name.</summary>
public sealed record GetGameNewsQuery(ulong GuildId) : IQuery<GameNewsSubscription[]>;

/// <summary>Creates a news channel and role for a Steam game and gives the creator the role.</summary>
public sealed record CreateGameNewsCommand(ulong GuildId, ulong UserId, uint AppId, string Name, GameNewsSources Sources = GameNewsSources.Official)
    : ICommand<GameNewsSubscription>;

/// <summary>Takes or drops the subscription's notification role.</summary>
public sealed record SubscribeGameNewsCommand(ulong GuildId, ulong UserId, uint AppId, bool Subscribe) : ICommand<GameNewsSubscription>;

/// <param name="IsAdmin">Server administrators may delete or change anyone's subscription; others only their own.</param>
public sealed record DeleteGameNewsCommand(ulong GuildId, ulong UserId, bool IsAdmin, uint AppId) : ICommand<GameNewsSubscription>;

/// <summary>
/// Chooses which kinds of news to post. Posts of a newly ticked kind that are already out count as delivered, so turning
/// on press articles doesn't flood the channel with months of old ones.
/// </summary>
public sealed record SetGameNewsSourcesCommand(ulong GuildId, ulong UserId, bool IsAdmin, uint AppId, GameNewsSources Sources)
    : ICommand<GameNewsSubscription>;

internal sealed class GameNewsHandlers(IGameNewsStore store, IGameNewsGateway gateway, IClock clock) :
    IQueryHandler<GetGameNewsQuery, GameNewsSubscription[]>, ICommandHandler<CreateGameNewsCommand, GameNewsSubscription>,
    ICommandHandler<SubscribeGameNewsCommand, GameNewsSubscription>, ICommandHandler<DeleteGameNewsCommand, GameNewsSubscription>,
    ICommandHandler<SetGameNewsSourcesCommand, GameNewsSubscription>
{
    // One change at a time, so two people creating the same game can't both make a channel.
    private static readonly SemaphoreSlim Changes = new(1);

    private static ErrorDescriptor Error(string code, string text) => new("GameNews." + code, text);
    private static Result<GameNewsSubscription> Fail(string code, string text) => Result.Failure<GameNewsSubscription>(Error(code, text));
    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    public async Task<Result<GameNewsSubscription[]>> Handle(GetGameNewsQuery query, CancellationToken cancellationToken) =>
        await ListAsync(query.GuildId, cancellationToken);

    public Task<Result<GameNewsSubscription>> Handle(CreateGameNewsCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var sources = command.Sources & GameNewsSources.All;
        if (sources == GameNewsSources.None)
            return Fail("Sources", "Pick at least one kind of news.");
        string name = (command.Name ?? "").Trim();
        if (command.AppId == 0 || name.Length is 0 or > 65)
            return Fail("Input", "Enter a Steam App ID or store link and a game name of up to 65 characters.");
        if ((await ListAsync(command.GuildId, cancellationToken)).Any(item => item.AppId == command.AppId))
            return Fail("Exists", "This game already has a subscription.");
        var (channelId, roleId) = await gateway.CreateAsync(command.GuildId, command.UserId, command.AppId, name, cancellationToken);
        var item = new GameNewsSubscription(Id(command.GuildId), command.AppId, name, Id(command.UserId), Id(channelId), Id(roleId),
            clock.UtcNow.ToUnixTimeSeconds(), Sources: sources);
        await store.AddAsync(item, cancellationToken);
        return item;
    });

    public Task<Result<GameNewsSubscription>> Handle(SubscribeGameNewsCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (await FindAsync(command.GuildId, command.AppId, cancellationToken) is not { } item)
            return Fail("Missing", "Subscription not found.");
        if (item.Deleting)
            return Fail("Deleting", "This subscription is being deleted.");
        await gateway.SetRoleAsync(command.GuildId, command.UserId, Parse(item.RoleId), command.Subscribe, cancellationToken);
        return item;
    });

    public Task<Result<GameNewsSubscription>> Handle(DeleteGameNewsCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (await FindAsync(command.GuildId, command.AppId, cancellationToken) is not { } item)
            return Fail("Missing", "Subscription not found.");
        if (item.OwnerId != Id(command.UserId) && !command.IsAdmin)
            return Fail("NotOwner", "Only the subscription creator or a server administrator can delete it.");
        // Marked first, so a failed Discord deletion leaves a visible "deleting" subscription that can be retried.
        await store.MarkDeletingAsync(item, cancellationToken);
        await gateway.DeleteAsync(command.GuildId, Parse(item.ChannelId), Parse(item.RoleId), cancellationToken);
        await store.RemoveAsync(item, cancellationToken);
        return item;
    });

    public Task<Result<GameNewsSubscription>> Handle(SetGameNewsSourcesCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var sources = command.Sources & GameNewsSources.All;
        if (sources == GameNewsSources.None)
            return Fail("Sources", "Pick at least one kind of news.");
        if (await FindAsync(command.GuildId, command.AppId, cancellationToken) is not { } item)
            return Fail("Missing", "Subscription not found.");
        if (item.Deleting)
            return Fail("Deleting", "This subscription is being deleted.");
        if (item.OwnerId != Id(command.UserId) && !command.IsAdmin)
            return Fail("NotOwnerSources", "Only the subscription creator or a server administrator can choose its news.");
        var added = sources & ~item.Sources;
        if (added != GameNewsSources.None)
            foreach (var post in await gateway.ReadPostsAsync(item.AppId, item.CreatedAt, cancellationToken))
                if ((post.Kind & added) != 0)
                    await store.RecordDeliveryAsync(item, post.Id, cancellationToken);
        await store.SetSourcesAsync(item, sources, cancellationToken);
        return item with { Sources = sources };
    });

    private async Task<GameNewsSubscription[]> ListAsync(ulong guildId, CancellationToken cancellationToken) =>
        (await store.ListAsync(cancellationToken)).Where(item => item.GuildId == Id(guildId))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private async Task<GameNewsSubscription?> FindAsync(ulong guildId, uint appId, CancellationToken cancellationToken) =>
        (await ListAsync(guildId, cancellationToken)).FirstOrDefault(item => item.AppId == appId);

    private static async Task<Result<GameNewsSubscription>> RunAsync(Func<Task<Result<GameNewsSubscription>>> action)
    {
        await Changes.WaitAsync();
        try
        {
            return await action();
        }
        catch (GameNewsException error)
        {
            return Result.Failure<GameNewsSubscription>(Error("Gateway", error.Message));
        }
        finally
        {
            Changes.Release();
        }
    }
}
