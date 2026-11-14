using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using HappyMix.Application.Guilds;

namespace HappyMix.Bot.Services;

internal sealed class DiscordGroupRoleGateway(DiscordSocketClient client) : IGroupRoleGateway
{
    public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) => RunAsync<IReadOnlyCollection<ulong>>(async () =>
        (await GetGuildAsync(guildId)).Roles.Select(role => role.Id).ToArray());

    public Task<ulong> CreateAsync(ulong guildId, string name) => RunAsync(async () =>
        (await (await GetGuildAsync(guildId)).CreateRoleAsync(name, permissions: GuildPermissions.None)).Id);

    public Task DeleteAsync(ulong guildId, ulong roleId) => RunAsync(async () =>
    {
        try
        {
            var role = (await GetGuildAsync(guildId)).Roles.FirstOrDefault(item => item.Id == roleId);
            if (role is not null)
                await role.DeleteAsync();
        }
        catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownRole)
        {
            // The role has already been deleted.
        }
        return true;
    });

    public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) => RunAsync(async () =>
    {
        var guild = await GetGuildAsync(guildId);
        if (userId is { } id)
        {
            try
            {
                var member = await guild.GetUserAsync(id);
                if (member is not null)
                    await ApplyToMemberAsync(member, getDelta);
            }
            catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownMember)
            {
                // Former server members have no roles to update.
            }
            return true;
        }

        await foreach (var page in guild.GetUsersAsync())
        {
            foreach (var member in page)
                await ApplyToMemberAsync(member, getDelta);
        }
        return true;
    });

    private static async Task ApplyToMemberAsync(RestGuildUser member,
        Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta)
    {
        var delta = getDelta(member.Id, member.RoleIds);
        try
        {
            foreach (ulong id in delta.Remove)
                await member.RemoveRoleAsync(id);
            foreach (ulong id in delta.Add)
                await member.AddRoleAsync(id);
        }
        catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownMember)
        {
            // The member left the server during the repair.
        }
    }

    private async Task<RestGuild> GetGuildAsync(ulong guildId) =>
        await client.Rest.GetGuildAsync(guildId);
    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (HttpException exception)
        {
            throw new GroupRoleException(
                $"Discord rejected the role operation ({exception.HttpCode}). Check Manage Roles and role hierarchy.", exception);
        }
    }
}
