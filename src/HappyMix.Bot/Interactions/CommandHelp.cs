using Discord.Interactions;

namespace HappyMix.Bot.Interactions;

internal sealed record CommandGuide(string Path, string Description, string Flow, SlashCommandParameterInfo[] Options);
internal static class CommandHelp
{
    internal static string Flow(string module) => module switch
    {
        "group" => "Create a group, invite a specific Discord account, and have that account accept. Members can then check rosters, games, and shared availability. The organizer or a moderator manages the group.",
        "availability" => "Set today's hours or a recurring weekday, then check your saved windows. Earlier end times roll into the next day. Weekly hours show as recurring on the group board.",
        "settings" => "Administrators can inspect server defaults, then set announcement channels, followed teams, notifications, and roles. Per-game settings can inherit the server or deployment defaults.",
        "links" => "Create a private approval link and open it while signed in as the alternate. Both accounts must belong to this server. Administrators can review and link server accounts from the dashboard.",
        _ => "Use /ping to check the bot or /help to browse commands. For details, enter a command path such as group invite."
    };

    internal static CommandGuide[] Read(InteractionService service) => service.Modules.Where(module => !module.IsSubModule)
        .SelectMany(module => ReadModule(module, "")).OrderBy(command => command.Path, StringComparer.Ordinal).ToArray();

    private static IEnumerable<CommandGuide> ReadModule(ModuleInfo module, string prefix)
    {
        if (module.IsSlashGroup)
            prefix += module.SlashGroupName + " ";
        foreach (var command in module.SlashCommands)
        {
            string path = prefix + command.Name;
            yield return new CommandGuide(path, command.Description, Details(path), command.Parameters.ToArray());
        }
        foreach (var child in module.SubModules)
            foreach (var command in ReadModule(child, prefix))
                yield return command;
    }

    private static string Details(string path) => path switch
    {
        "group invite" => "Select the exact account that should receive the DM. Only that account can respond and receive the group role.",
        "group join" or "group decline" => "Use the group name from the invitation sent to your current account. You can also respond using the DM buttons.",
        "group availability" => "Choose a group and optionally a date within the next seven days. Times use the server zone. Recurring entries are labeled separately.",
        "group games" => "List attached games and copy an ID for game-edit or game-remove.",
        "group game-add" or "group game-edit" => "The organizer or a moderator can supply a game name and an optional Steam app ID or store URL.",
        "group game-remove" => "Copy the game ID from /group games, then remove that attachment.",
        "group repair-roles" => "Moderators can reconcile the recorded group roles for one account or everyone using the buttons.",
        "group create" => "Choose a unique name. Your server may require an organizer role. Add a description or the first game if needed.",
        "group delete" => "The organizer or a moderator closes the group and removes its linked Discord role.",
        "availability edit" or "availability remove" => "Copy the window ID from /availability list. Use a full date, local time, and offset for each replacement time.",
        "availability set today" or "availability set timetable" => "Use times like 17 and 00:30, or choose all_day. Repeat to add separate ranges. Set tentative to true for uncertain hours. Supply a named time zone or fixed offset if the server zone does not suit you.",
        "settings teams" or "settings twitch" => "Choose a game, then enter a comma-separated list. Use default to inherit or none to clear the selection.",
        "settings channel" => "Choose a standard text channel where HappyMix can post embeds. Omit the channel with a game selected to inherit the server default.",
        "links request" => "Only share the private approval link with the person who controls the alternate account. Approval and conflict review happen in the browser.",
        "links unlink" => "Review the schedule and memberships that the alternate keeps before confirming in the browser.",
        "group me" or "group list" => "Use page to browse more results. List also includes invitations addressed to your current account.",
        "group leave" => "Leave the shared membership from any linked account. Organizers must close the group instead.",
        "group remove" => "Select a current member or invitee. Removing a linked member removes the shared membership.",
        "group edit-description" => "The organizer or a moderator can replace the description. Omit it to clear the current text.",
        "availability add" => "Enter both dates with offsets, for example 2026-11-14 18:00 +02:00.",
        "availability list" => "Use page to browse upcoming windows. Copy a window ID to edit or remove it.",
        "availability remove-weekly" => "Copy the day, start and exact time zone from /availability timetable to remove only that range.",
        "availability clear-day" => "Choose the weekday whose recurring hours should be removed.",
        "availability timetable" => "Review the saved weekday schedule. Use /availability set timetable to add another range. The same day, start and zone updates that range.",
        "availability dashboard" or "links dashboard" => "Open the private dashboard link and sign in with Discord.",
        "settings show" => "Check the current server settings before changing channels or notifications.",
        "settings set" => "Choose the default announcement channel and an IANA time zone such as Europe/Kyiv.",
        "settings timezone" => "Use an IANA time zone or UTC. This changes the default for availability and esports schedules.",
        "settings add-team" => "Choose a game and a friendly name, then supply the team's positive PandaScore ID.",
        "settings remove-team" => "Use /settings team-catalog to find the alias. Removing it leaves the saved followed team IDs intact.",
        "settings reset-catalog" or "settings reset-channel" => "Use /settings show to review settings after restoring the deployment default.",
        "settings team-catalog" => "Browse the aliases for the selected game. Use those names or their IDs with /settings teams.",
        "settings notifications" => "Set the schedule, reminders, map results, match results, and match-start toggles in one command.",
        "settings reminder" => "Set the lead time in minutes. Enable reminders under /settings notifications.",
        "settings organizer-role" => "Add or remove a single server role, or clear the list to allow any server member to organize a group.",
        "settings ping-role" => "Choose which role match reminders can mention. Omit the role to clear it.",
        "links show" => "Review your linked accounts and copy a pending request ID if you need to revoke it.",
        "links revoke" => "Copy an open request ID from /links show. Revocation invalidates the private approval link.",
        "links manage" => "Administrators select two server accounts, then review schedule conflicts and confirm in the browser.",
        "ping" => "The private reply includes the time when the bot checked its status.",
        "help" => "Run /help for every command, /help topic:group for a module, or /help topic:group invite for options and instructions.",
        _ => ""
    };
}
