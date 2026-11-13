using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Bot.Interactions;

[Group("group", "Manage saved gaming groups in this server.")]
[RequireContext(ContextType.Guild)]
public sealed class GuildGroupModule(IGuildGroupStore groups, IGuildSettingsStore settings)
    : InteractionModuleBase<SocketInteractionContext>
{
    private bool CanModerate => Context.User is SocketGuildUser user &&
        (user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator);

    [SlashCommand("create", "Create an invite-only gaming group.")]
    public async Task CreateAsync(string name)
    {
        var saved = await settings.GetAsync(Context.Guild.Id);
        var roles = saved?.Preferences?.OrganizerRoleIds;
        if (!CanModerate && roles is { Length: > 0 } &&
            (Context.User is not SocketGuildUser user || !user.Roles.Any(role => roles.Contains(role.Id))))
        {
            await RespondAsync("You need a configured organizer role to create a group.", ephemeral: true);
            return;
        }

        await UpdateAsync(name, group => group is null
            ? new GuildGroup(name.Trim(), Context.User.Id, [Context.User.Id], [])
            : throw new GroupCommandException("A group with that name already exists."), "Group created.");
    }

    [SlashCommand("invite", "Invite a server member to a group.")]
    public async Task InviteAsync(string name, IUser member)
    {
        if (member.IsBot || member is not IGuildUser guildMember || guildMember.GuildId != Context.Guild.Id)
        {
            await RespondAsync("Choose a human member of this server.", ephemeral: true);
            return;
        }

        Guid token = Guid.NewGuid();
        await UpdateAsync(name, existing =>
        {
            var group = RequireManagedGroup(existing);
            if (group.Members.Contains(member.Id))
                throw new GroupCommandException("That member already belongs to the group.");

            Dictionary<ulong, Guid> tokens = group.InviteTokens is { } saved ? new(saved) : [];
            tokens[member.Id] = token;
            return group with { Invites = group.Invites.Append(member.Id).Distinct().ToArray(), InviteTokens = tokens };
        }, async () =>
        {
            var buttons = new ComponentBuilder()
                .WithButton("Accept", $"group-invite:accept:{Context.Guild.Id}:{token:N}", ButtonStyle.Success)
                .WithButton("Decline", $"group-invite:decline:{Context.Guild.Id}:{token:N}", ButtonStyle.Secondary)
                .Build();
            try
            {
                var dm = await member.CreateDMChannelAsync();
                await dm.SendMessageAsync(
                    $"You’re invited to **{name.Trim()}** in **{Format.Sanitize(Context.Guild.Name)}**.",
                    components: buttons, allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { Timeout = 10000 });
                return "Invitation saved and sent by DM.";
            }
            catch (Discord.Net.HttpException)
            {
                return "Invitation saved. DM delivery failed. The member can use /group list, then /group join or /group decline.";
            }
            catch (TimeoutException)
            {
                return "Invitation saved. DM delivery timed out. The member can use /group list, then /group join or /group decline.";
            }
        });
    }

    [SlashCommand("join", "Accept a group invitation.")]
    public Task JoinAsync(string name) => ResolveInviteAsync(name, accept: true);

    [SlashCommand("decline", "Decline a group invitation.")]
    public Task DeclineAsync(string name) => ResolveInviteAsync(name, accept: false);

    private Task ResolveInviteAsync(string name, bool accept) => UpdateAsync(name,
        existing => GroupInvitation.Resolve(RequireGroup(existing), Context.User.Id, accept)
            ?? throw new GroupCommandException("You have no pending invitation to this group."),
        accept ? "You joined the group." : "Invitation declined.");

    [SlashCommand("leave", "Leave a group.")]
    public Task LeaveAsync(string name) => UpdateAsync(name, existing =>
    {
        var group = RequireGroup(existing);
        ulong userId = Context.User.Id;
        if (group.OrganizerId == userId)
            throw new GroupCommandException("You organize this group. Use /group delete to close it.");
        if (!group.Members.Contains(userId))
            throw new GroupCommandException("You are not a member of this group.");
        return group with { Members = group.Members.Where(id => id != userId).ToArray() };
    }, "You left the group.");

    [SlashCommand("remove", "Remove a member or revoke an invitation.")]
    public Task RemoveAsync(string name, IUser member) =>
        ManageAsync(name, group =>
        {
            if (group.OrganizerId == member.Id)
                throw new GroupCommandException("The organizer owns this group. Use /group delete to close it.");
            return group with
            {
                Members = group.Members.Where(id => id != member.Id).ToArray(),
                Invites = group.Invites.Where(id => id != member.Id).ToArray(),
                InviteTokens = GroupInvitation.RemoveToken(group, member.Id)
            };
        }, "Membership or invitation removed.");

    [SlashCommand("delete", "Close a saved group.")]
    public Task DeleteAsync(string name) => ManageAsync(name, _ => null, "Group deleted.");

    [SlashCommand("list", "List your groups and invitations.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var visible = (await groups.ListAsync(Context.Guild.Id))
            .Where(group => CanModerate || group.Members.Contains(Context.User.Id) || group.Invites.Contains(Context.User.Id))
            .ToArray();
        const int pageSize = 15;
        int pages = Math.Max(1, (visible.Length + pageSize - 1) / pageSize);
        string message = page < 1 || page > pages ? $"Choose a page from 1 to {pages}."
            : visible.Length == 0 ? "You have no groups or invitations in this server."
            : string.Join("\n", visible.Skip((page - 1) * pageSize).Take(pageSize).Select(group =>
                $"**{group.Name}** В· {group.Members.Length} members" +
                (group.Invites.Contains(Context.User.Id) ? " В· invited" : "")));
        await ReplyAsync(message);
    }

    private Task ManageAsync(string name, Func<GuildGroup, GuildGroup?> update, string message) =>
        UpdateAsync(name, existing => update(RequireManagedGroup(existing)), message);

    private GuildGroup RequireManagedGroup(GuildGroup? existing)
    {
        var group = RequireGroup(existing);
        if (group.OrganizerId != Context.User.Id && !CanModerate)
            throw new GroupCommandException("Only the group's organizer or a server moderator can manage it.");
        return group;
    }

    private Task UpdateAsync(string name, Func<GuildGroup?, GuildGroup?> update, string message) =>
        UpdateAsync(name, update, () => Task.FromResult(message));

    private async Task UpdateAsync(string name, Func<GuildGroup?, GuildGroup?> update, Func<Task<string>> onSaved)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Any(character =>
            !char.IsLetterOrDigit(character) && character is not (' ' or '-' or '_')))
        {
            await RespondAsync("Use a group name of 1 to 40 letters, digits, spaces, hyphens or underscores.",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        string message;
        try
        {
            await groups.UpdateAsync(Context.Guild.Id, name, update);
            message = await onSaved();
        }
        catch (GroupCommandException exception)
        {
            message = exception.Message;
        }
        await ReplyAsync(message);
    }

    private Task<IUserMessage> ReplyAsync(string message) => ModifyOriginalResponseAsync(response =>
    {
        response.Content = message;
        response.AllowedMentions = AllowedMentions.None;
    });

    private static GuildGroup RequireGroup(GuildGroup? group) =>
        group ?? throw new GroupCommandException("That group does not exist in this server.");

    private sealed class GroupCommandException(string message) : Exception(message);
}
