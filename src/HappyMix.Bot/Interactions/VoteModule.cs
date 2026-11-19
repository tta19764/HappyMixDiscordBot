using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Polls;
using HappyMix.Bot.Services;
using MediatR;
using Microsoft.Extensions.Options;
using Poll = HappyMix.Application.Abstractions.Polls.Poll;

namespace HappyMix.Bot.Interactions;

/// <summary>Votes in Discord: <c>/vote</c> commands and the menu and button on vote messages.</summary>
[Group("vote", "Start, find or end votes.")]
[RequireContext(ContextType.Guild)]
public sealed class VoteModule(ISender sender, PollMessages messages, IOptions<CommunityDefaults> community)
    : InteractionModuleBase<SocketInteractionContext>
{
    private string Language => community.Value.LanguageCode;

    [SlashCommand("create", "Start a vote in this channel. Separate options with ;")]
    public async Task CreateAsync(
        [Summary(description: "What to vote on, e.g. What are we playing tonight?"), MaxLength(PollRules.QuestionLength)] string question,
        [Summary(description: "2-10 options separated by ;, titles or Steam/IMDb/MyAnimeList links")] string options,
        [Summary(description: "Only members of this group vote"), Autocomplete(typeof(VoteGroupAutocomplete))] string? group = null,
        [Summary(description: "When voting ends"), Choice("1 hour", 60), Choice("3 hours", 180), Choice("12 hours", 720),
         Choice("1 day", 1440), Choice("3 days", 4320), Choice("1 week", 10080)] int duration = 1440,
        [Summary(description: "Allow ticking several options")] bool multiple = false)
    {
        await DeferAsync(ephemeral: true);
        Guid? groupId = Guid.TryParse(group, out var parsed) ? parsed : null;
        var result = await sender.Send(new CreatePollCommand(Actor(), groupId, question, options.Split(';', StringSplitOptions.TrimEntries), multiple,
            TimeSpan.FromMinutes(duration), [], Everyone: groupId is null, Guid.NewGuid(), Context.Channel.Id));
        if (result.IsFailure)
        {
            await FollowupAsync(Message(result.Error.Description), ephemeral: true);
            return;
        }
        await messages.SyncAsync(result.Value, Context.Guild.Id);
        await FollowupAsync(NotificationText.Get(Language, "Polls_Started"), ephemeral: true);
    }

    [SlashCommand("list", "Show open votes you can take part in.")]
    public async Task ListAsync()
    {
        await DeferAsync(ephemeral: true);
        var open = await OpenPollsAsync(sender, Actor());
        if (open.Count == 0)
        {
            await FollowupAsync(NotificationText.Get(Language, "Polls_NoneOpen"), ephemeral: true);
            return;
        }
        var lines = open.Take(15).Select(poll => $"• **{Format.Sanitize(poll.Question)}** · <t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>"
            + (poll.ChannelId is { } channel && poll.MessageId is { } message ? $" · https://discord.com/channels/{poll.GuildId}/{channel}/{message}" : ""));
        await FollowupAsync(string.Join("\n", lines), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("end", "End a vote you started before its time.")]
    public async Task EndAsync([Summary(description: "The vote to end"), Autocomplete(typeof(VotePollAutocomplete))] string vote)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(vote, out var pollId))
        {
            await FollowupAsync(NotificationText.Get(Language, "Polls_PickFromList"), ephemeral: true);
            return;
        }
        var result = await sender.Send(new ClosePollCommand(Actor(), pollId));
        if (result.IsSuccess)
            await messages.SyncAsync(result.Value);
        await FollowupAsync(result.IsSuccess ? NotificationText.Get(Language, "Polls_Ended") : Message(result.Error.Description), ephemeral: true);
    }

    [ComponentInteraction("poll:pick:*:*", ignoreGroupNames: true)]
    public Task PickAsync(string guild, string poll, string[] choices) =>
        AnswerAsync(guild, poll, choices.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray());

    [ComponentInteraction("poll:clear:*:*", ignoreGroupNames: true)]
    public Task ClearAsync(string guild, string poll) => AnswerAsync(guild, poll, []);

    private async Task AnswerAsync(string guild, string pollText, int[] positions)
    {
        await DeferAsync(ephemeral: true);
        if (!ulong.TryParse(guild, out var guildId) || guildId != Context.Guild.Id || !Guid.TryParseExact(pollText, "N", out var pollId))
        {
            await FollowupAsync("Invalid vote.", ephemeral: true);
            return;
        }
        var result = await sender.Send(new VotePollCommand(Actor(), pollId, positions));
        if (result.IsFailure)
        {
            await FollowupAsync(Message(result.Error.Description), ephemeral: true);
            return;
        }
        var poll = result.Value;
        string chosen = string.Join(", ", poll.Options.Where(option => positions.Contains(option.Position)).Select(option => option.Label));
        await FollowupAsync(positions.Length == 0 ? NotificationText.Get(Language, "Polls_VoteRemoved") : NotificationText.Get(Language, "Polls_YouPicked", chosen),
            ephemeral: true, allowedMentions: AllowedMentions.None);
        await messages.SyncAsync(poll);
    }

    private GroupActor Actor() => new(Context.Guild.Id, Context.User.Id,
        Context.User is SocketGuildUser member && (member.GuildPermissions.ManageGuild || member.GuildPermissions.Administrator));

    /// <summary>Application errors are resource keys or English text; both have entries in the notification resources.</summary>
    private string Message(string error) => NotificationText.Get(Language, NotificationText.KeyFor(error));

    /// <summary>Open votes of the server and of the member's groups.</summary>
    internal static async Task<IReadOnlyList<Poll>> OpenPollsAsync(ISender sender, GroupActor actor)
    {
        var open = new List<Poll>();
        if ((await sender.Send(new GetPollBoardQuery(actor, null))).Value is { } server)
            open.AddRange(server.Open);
        var groups = (await sender.Send(new GetGroupDirectoryQuery(actor, GroupDirectoryFilter.Mine, null, 1, 50))).Value;
        foreach (var group in groups?.Items ?? [])
            if (await sender.Send(new GetPollBoardQuery(actor, group.Id)) is { IsSuccess: true } board)
                open.AddRange(board.Value.Open);
        return open.OrderBy(poll => poll.ClosesAt).ToArray();
    }
}

/// <summary>The member's groups, for <c>/vote create group</c>.</summary>
public sealed class VoteGroupAutocomplete(ISender sender) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.User is not SocketGuildUser member)
            return AutocompletionResult.FromSuccess();
        string typed = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var actor = new GroupActor(member.Guild.Id, member.Id, member.GuildPermissions.ManageGuild || member.GuildPermissions.Administrator);
        var groups = (await sender.Send(new GetGroupDirectoryQuery(actor, GroupDirectoryFilter.Mine, typed.Length == 0 ? null : typed, 1, 25))).Value;
        return AutocompletionResult.FromSuccess((groups?.Items ?? []).Take(25)
            .Select(group => new AutocompleteResult(group.Name.Length <= 100 ? group.Name : group.Name[..99] + "…", group.Id.ToString())));
    }
}

/// <summary>Open votes the member can end, for <c>/vote end</c>.</summary>
public sealed class VotePollAutocomplete(ISender sender) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.User is not SocketGuildUser member)
            return AutocompletionResult.FromSuccess();
        string typed = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? "";
        var actor = new GroupActor(member.Guild.Id, member.Id, member.GuildPermissions.ManageGuild || member.GuildPermissions.Administrator);
        var open = await VoteModule.OpenPollsAsync(sender, actor);
        return AutocompletionResult.FromSuccess(open
            .Where(poll => typed.Length == 0 || poll.Question.Contains(typed, StringComparison.CurrentCultureIgnoreCase))
            .Take(25).Select(poll => new AutocompleteResult(poll.Question.Length <= 100 ? poll.Question : poll.Question[..99] + "…", poll.Id.ToString())));
    }
}
