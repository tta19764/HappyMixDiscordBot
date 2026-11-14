using Discord;
using Discord.Interactions;
using HappyMix.Application.Guilds;
using MediatR;

namespace HappyMix.Bot.Interactions;

[Group("links", "Manage linked Discord accounts for this server.")]
[RequireContext(ContextType.Guild)]
public sealed class LinkedAccountsModule(ISender sender, IConfiguration configuration)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("show", "Show your primary account, alternates, and pending requests.")]
    public async Task ShowAsync()
    {
        await DeferAsync(ephemeral: true);
        var view = (await sender.Send(new GetLinkedAccountsQuery(Context.Guild.Id, Context.User.Id))).Value;
        var alternate = string.Join(", ", view.Alternates.Select(account => MentionUtils.MentionUser(account.AlternateId)));
        string requests = string.Join(", ", view.Requests.Select(request => $"`{request.Id}` (expires <t:{request.ExpiresAt.ToUnixTimeSeconds()}:R>)"));
        await ReplyAsync($"Primary: {MentionUtils.MentionUser(view.PrimaryId)}\nAlternates: {(alternate.Length == 0 ? "None" : alternate)}\nPending requests: {(requests.Length == 0 ? "None" : requests)}");
    }

    [SlashCommand("request", "Create a private link for your alternate to approve in the browser.")]
    public async Task RequestAsync()
    {
        if (DashboardUrl() is not { } origin)
        {
            await RespondAsync("The web dashboard must be enabled to approve links.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new CreateAccountLinkRequestCommand(Context.Guild.Id, Context.User.Id));
        if (result.IsFailure)
        {
            await ReplyAsync(result.Error.Description);
            return;
        }
        var request = result.Value;
        var url = new Uri(origin, $"/links/{Context.Guild.Id}/approve/{request.RequestId}?t={Uri.EscapeDataString(request.Token)}");
        await ReplyAsync($"Open this private link while signed in as the alternate. It expires <t:{request.ExpiresAt.ToUnixTimeSeconds()}:R>.\n<{url.AbsoluteUri}>\nRequest ID: `{request.RequestId}`");
    }

    [SlashCommand("revoke", "Revoke an open account link request using its ID.")]
    public async Task RevokeAsync(string id)
    {
        if (!Guid.TryParse(id, out var request))
        {
            await RespondAsync("Enter the request ID from /links request.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new RevokeAccountLinkRequestCommand(Context.Guild.Id, Context.User.Id, request));
        await ReplyAsync(result.IsSuccess ? "Link request revoked." : result.Error.Description);
    }

    [SlashCommand("unlink", "Review and confirm unlinking an alternate in the dashboard.")]
    public Task UnlinkAsync(IUser alternate) => OpenAsync($"/unlink/{alternate.Id}");

    [SlashCommand("dashboard", "Open the linked accounts dashboard.")]
    public Task DashboardAsync() => OpenAsync("");

    [SlashCommand("manage", "Open the administrator account-linking review.")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public Task ManageAsync(IUser primary, IUser alternate) =>
        OpenAsync($"/admin?primary={primary.Id}&alternate={alternate.Id}");

    private Task OpenAsync(string path) => RespondAsync(DashboardUrl() is { } origin
        ? $"Open <{new Uri(origin, $"/links/{Context.Guild.Id}{path}").AbsoluteUri}>."
        : "The web dashboard is unavailable.", ephemeral: true);

    private Uri? DashboardUrl() => configuration.GetValue<bool>("Dashboard:Enabled") &&
        Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        ? uri : null;

    private Task<IUserMessage> ReplyAsync(string message) => ModifyOriginalResponseAsync(response =>
    {
        response.Content = message;
        response.AllowedMentions = AllowedMentions.None;
    });
}
