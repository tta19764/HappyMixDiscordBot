using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.WebSocket;

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

/// <summary>Sends the Accept/Decline direct message used by both the slash command and the dashboard.</summary>
internal static class GroupInviteMessages
{
    public static async Task<bool> SendAsync(IUser member, ulong guildId, string guildName, string groupName, Guid token)
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
