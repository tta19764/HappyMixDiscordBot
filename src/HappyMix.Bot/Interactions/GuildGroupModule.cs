using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Guilds;
using MediatR;

namespace HappyMix.Bot.Interactions;

[Group("group", "Manage saved gaming groups in this server.")]
[RequireContext(ContextType.Guild)]
public sealed class GuildGroupModule(ISender sender)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const int DescriptionLimit = 200;

    private bool CanModerate => Context.User is SocketGuildUser user &&
        (user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator);

    [SlashCommand("create", "Create an invite-only gaming group.")]
    public Task CreateAsync(string name, [MaxLength(DescriptionLimit)] string? description = null) =>
        ExecuteAsync(new CreateGroupCommand(Actor, name, description,
            (Context.User as IGuildUser)?.RoleIds.ToArray() ?? []), "Group created.");

    private GroupActor Actor => new(Context.Guild.Id, Context.User.Id, CanModerate);

    [SlashCommand("invite", "Invite a server member to a group.")]
    public async Task InviteAsync([Autocomplete<GroupNameAutocomplete>] string name, IUser member)
    {
        if (member.IsBot || member is not IGuildUser guildMember || guildMember.GuildId != Context.Guild.Id)
        {
            await RespondAsync("Choose a human member of this server.", ephemeral: true);
            return;
        }

        Guid token = Guid.NewGuid();
        await ExecuteAsync(new InviteGroupMemberCommand(Actor, name, member.Id, token), async () =>
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
    public Task JoinAsync([Autocomplete<GroupNameAutocomplete>] string name) => ResolveInviteAsync(name, accept: true);

    [SlashCommand("decline", "Decline a group invitation.")]
    public Task DeclineAsync([Autocomplete<GroupNameAutocomplete>] string name) => ResolveInviteAsync(name, accept: false);

    private Task ResolveInviteAsync(string name, bool accept) =>
        ExecuteAsync(new ResolveGroupInviteCommand(Actor, name, null, accept),
            accept ? "You joined the group." : "Invitation declined.");

    [SlashCommand("leave", "Leave a group.")]
    public Task LeaveAsync([Autocomplete<GroupNameAutocomplete>] string name) =>
        ExecuteAsync(new LeaveGroupCommand(Actor, name), "You left the group.");

    [SlashCommand("remove", "Remove a member or revoke an invitation.")]
    public Task RemoveAsync([Autocomplete<GroupNameAutocomplete>] string name, IUser member) =>
        ExecuteAsync(new RemoveGroupMemberCommand(Actor, name, member.Id), "Membership or invitation removed.");

    [SlashCommand("delete", "Close a saved group.")]
    public Task DeleteAsync([Autocomplete<GroupNameAutocomplete>] string name) =>
        ExecuteAsync(new DeleteGroupCommand(Actor, name), "Group deleted.");

    [SlashCommand("repair-roles", "Open the group role repair buttons.")]
    public async Task RepairRolesAsync(IUser? member = null)
    {
        if (!CanModerate)
        {
            await RespondAsync("Only server moderators can repair group roles.", ephemeral: true);
            return;
        }

        var buttons = new ComponentBuilder()
            .WithButton("Repair all members", "group-role-repair:all", ButtonStyle.Primary);
        if (member is not null)
            buttons.WithButton("Repair selected member", $"group-role-repair:{member.Id}");
        await RespondAsync(member is null ? "Repair group roles for the server."
            : $"Repair group roles for **{Format.Sanitize(member.Username)}** or the whole server.",
            components: buttons.Build(), allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    [ComponentInteraction("group-role-repair:*", ignoreGroupNames: true)]
    public async Task RepairRolesButtonAsync(string target)
    {
        if (Context.Guild is null || !CanModerate)
        {
            await RespondAsync("Only server moderators can repair group roles.", ephemeral: true);
            return;
        }

        ulong? memberId = null;
        if (target != "all")
        {
            if (!ulong.TryParse(target, out ulong id))
            {
                await RespondAsync("Invalid member selection.", ephemeral: true);
                return;
            }
            memberId = id;
        }

        await ExecuteAsync(new RepairGroupRolesCommand(Actor, memberId), "Group roles repaired.");
    }

    [SlashCommand("edit-description", "Edit a group description. Omit the description to clear it.")]
    public Task EditDescriptionAsync([Autocomplete<GroupNameAutocomplete>] string name,
        [MaxLength(DescriptionLimit)] string? description = null) =>
        ExecuteAsync(new EditGroupDescriptionCommand(Actor, name, description), "Group description updated.");

    [SlashCommand("me", "List groups you belong to in this server.")]
    public Task MeAsync([MinValue(1)] int page = 1) => ListGroupsAsync(page, membersOnly: true);

    [SlashCommand("list", "List your groups, their members, and pending invitations.")]
    public Task ListAsync([MinValue(1)] int page = 1) => ListGroupsAsync(page, membersOnly: false);

    private async Task ListGroupsAsync(int page, bool membersOnly)
    {
        await DeferAsync(ephemeral: true);
        var visible = (await sender.Send(new GetVisibleGroupsQuery(Actor, membersOnly))).Value;
        if (visible.Length == 0)
        {
            await ReplyAsync(membersOnly ? "You don't belong to any groups in this server."
                : "You have no groups or invitations in this server.");
            return;
        }

        var sections = visible.SelectMany(group =>
        {
            string heading = $"**{group.Name}** · {group.Members.Length} members" +
                (!membersOnly && group.Invites.Contains(Context.User.Id) ? " · invited" : "");
            if (group.Description is { } description)
                heading += $"\n{description}";

            if (membersOnly || group.Members.Length == 0)
                return [heading];

            return group.Members.Chunk(40).Select((members, index) =>
                (index == 0 ? heading : $"**{group.Name}** · continued") + "\n" +
                string.Join(", ", members.Select(MentionUtils.MentionUser)));
        });

        var pages = new List<string>();
        string current = "";
        foreach (string section in sections)
        {
            if (current.Length + section.Length + 2 > 1900)
            {
                pages.Add(current);
                current = "";
            }
            current = current.Length == 0 ? section : $"{current}\n\n{section}";
        }
        pages.Add(current);

        await ReplyAsync(page < 1 || page > pages.Count ? $"Choose a page from 1 to {pages.Count}."
            : pages[page - 1] + (pages.Count > 1 ? $"\n\nPage {page} of {pages.Count}" : ""));
    }

    private Task ExecuteAsync(GroupCommand command, string message) =>
        ExecuteAsync(command, () => Task.FromResult(message));

    private async Task ExecuteAsync(GroupCommand command, Func<Task<string>> onSuccess)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(command);
        if (result.IsFailure)
        {
            await ReplyAsync(result.Error.Description);
            return;
        }

        string message = await onSuccess();
        if (result.Value.RoleSyncFailed)
            message += " Use /group repair-roles to restore the Discord roles.";
        await ReplyAsync(message);
    }

    private Task<IUserMessage> ReplyAsync(string message) => ModifyOriginalResponseAsync(response =>
    {
        response.Content = message;
        response.AllowedMentions = AllowedMentions.None;
    });
}
