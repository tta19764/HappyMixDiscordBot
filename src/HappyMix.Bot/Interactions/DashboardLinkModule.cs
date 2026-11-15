using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace HappyMix.Bot.Interactions;

public sealed class DashboardLinkModule(IConfiguration configuration) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("dashboard", "Get the link to the HappyMix dashboard.")]
    public async Task DashboardAsync()
    {
        if (!configuration.GetValue<bool>("Dashboard:Enabled") ||
            !Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps)
        {
            await RespondAsync("The web dashboard is unavailable.", ephemeral: true);
            return;
        }

        var link = Context.Guild is { } guild ? new Uri(origin, $"/availability/{guild.Id}") : origin;
        await RespondAsync("Open HappyMix in your browser.", components: new ComponentBuilder()
            .WithButton("Open dashboard", style: ButtonStyle.Link, url: link.AbsoluteUri).Build(), ephemeral: true);
    }
}
