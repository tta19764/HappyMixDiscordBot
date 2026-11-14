using Discord;
using Discord.Interactions;
using HappyMix.Application.Guilds;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Bot.Interactions;

public sealed class GroupNameAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.Guild is null || parameter.Command is not SlashCommandInfo command)
            return AutocompletionResult.FromSuccess([]);

        ulong userId = context.User.Id;
        bool canModerate = context.User is IGuildUser user &&
            (user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator);
        string search = (autocompleteInteraction.Data.Current.Value as string)?.Trim() ?? "";
        var sender = services.GetRequiredService<ISender>();
        var result = await sender.Send(new SuggestGroupNamesQuery(
            new GroupActor(context.Guild.Id, userId, canModerate), command.Name, search));
        return AutocompletionResult.FromSuccess(result.Value.Select(name => new AutocompleteResult(name, name)));
    }
}
