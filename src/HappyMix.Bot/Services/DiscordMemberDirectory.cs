using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MediatR;

namespace HappyMix.Bot.Services;

/// <summary>Display data for a Discord user, or a fallback for someone who left the server.</summary>
public sealed record MemberProfile(ulong Id, string DisplayName, string? Username, string? AvatarUrl, bool InServer, bool IsBot)
{
    public static MemberProfile Missing(ulong id) => new(id, "", null, null, InServer: false, IsBot: false);
}

/// <summary>Resolves group rosters without one Discord request per member.</summary>
public interface IMemberDirectory
{
    /// <summary>Server members keyed by ID. <c>Available</c> is false when Discord refused the member list.</summary>
    Task<(IReadOnlyDictionary<ulong, MemberProfile> Members, bool Available)> GetMembersAsync(ulong guildId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lists server members with paged REST calls (1,000 per page) and caches the result briefly.
/// Listing members needs the Server Members Intent in the Developer Portal; without it rosters fall back to IDs.
/// </summary>
internal sealed class DiscordMemberDirectory(DiscordSocketClient client) : IMemberDirectory
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<ulong, (DateTimeOffset Loaded, IReadOnlyDictionary<ulong, MemberProfile> Members)> cache = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> locks = new();

    public async Task<(IReadOnlyDictionary<ulong, MemberProfile> Members, bool Available)> GetMembersAsync(ulong guildId,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(guildId, out var hit) && DateTimeOffset.UtcNow - hit.Loaded < Lifetime)
            return (hit.Members, true);

        var gate = locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(guildId, out hit) && DateTimeOffset.UtcNow - hit.Loaded < Lifetime)
                return (hit.Members, true);

            var members = new Dictionary<ulong, MemberProfile>();
            var guild = await client.Rest.GetGuildAsync(guildId);
            await foreach (var page in guild.GetUsersAsync().WithCancellation(cancellationToken))
            {
                foreach (var member in page)
                    members[member.Id] = new MemberProfile(member.Id, member.DisplayName, member.Username,
                        member.GetDisplayAvatarUrl(size: 64), InServer: true, member.IsBot);
            }

            cache[guildId] = (DateTimeOffset.UtcNow, members);
            return (members, true);
        }
        catch (HttpException)
        {
            return (new Dictionary<ulong, MemberProfile>(), false);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Sends the Accept/Decline direct message used by both the slash command and the dashboard. During the member's
/// do-not-disturb it waits (and counts as delivered); it goes out afterwards only if the invitation is still open.
/// </summary>
internal static class GroupInviteMessages
{
    public static async Task<bool> SendAsync(ISender sender, IUser member, ulong guildId, string guildName, string groupName, Guid token)
    {
        var payload = new HappyMix.Application.Quiet.HeldGroupInvite(guildId, guildName, groupName, member.Id, token);
        if ((await sender.Send(new HappyMix.Application.Quiet.HoldIfQuietCommand(member.Id, HappyMix.Application.Quiet.HeldKinds.GroupInvite,
                token.ToString("N"), System.Text.Json.JsonSerializer.Serialize(payload, HappyMix.Application.Quiet.HeldKinds.Json)))).Value)
            return true;
        return await SendNowAsync(member, guildId, guildName, groupName, token);
    }

    public static async Task<bool> SendNowAsync(IUser member, ulong guildId, string guildName, string groupName, Guid token)
    {
        var buttons = new ComponentBuilder()
            .WithButton("Accept", $"group-invite:accept:{guildId}:{token:N}", ButtonStyle.Success)
            .WithButton("Decline", $"group-invite:decline:{guildId}:{token:N}", ButtonStyle.Secondary)
            .Build();
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync($"You’re invited to **{Format.Sanitize(groupName.Trim())}** in **{Format.Sanitize(guildName)}**.",
                components: buttons, allowedMentions: AllowedMentions.None, options: new RequestOptions { Timeout = 10000 });
            return true;
        }
        catch (HttpException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>Sends group invitations that waited for the member's do-not-disturb to end.</summary>
internal sealed class HeldGroupInviteSender(DiscordSocketClient discord) : HappyMix.Application.Abstractions.Quiet.IHeldMessageSender
{
    public string Kind => HappyMix.Application.Quiet.HeldKinds.GroupInvite;

    public async Task<HappyMix.Application.Abstractions.Quiet.HeldDelivery> SendAsync(HappyMix.Application.Abstractions.Quiet.HeldMessage message,
        CancellationToken cancellationToken)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return HappyMix.Application.Abstractions.Quiet.HeldDelivery.RetryLater;
        if (System.Text.Json.JsonSerializer.Deserialize<HappyMix.Application.Quiet.HeldGroupInvite>(message.Payload, HappyMix.Application.Quiet.HeldKinds.Json) is not { } invite
            || await discord.Rest.GetUserAsync(invite.UserId, new RequestOptions { CancelToken = cancellationToken }) is not { } user)
            return HappyMix.Application.Abstractions.Quiet.HeldDelivery.Dropped;
        return await GroupInviteMessages.SendNowAsync(user, invite.GuildId, invite.GuildName, invite.GroupName, invite.Token)
            ? HappyMix.Application.Abstractions.Quiet.HeldDelivery.Sent : HappyMix.Application.Abstractions.Quiet.HeldDelivery.Dropped;
    }
}
