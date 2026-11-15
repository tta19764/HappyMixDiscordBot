using System.Globalization;
using Discord.Interactions;
using HappyMix.Bot.Interactions;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed partial class DashboardController
{
    // The guide isn't tied to a server, so it links back to the last server the member opened, when they still belong to it.
    [HttpGet("/info")]
    public async Task<IActionResult> Info([FromServices] InteractionService interactions, ulong? guildId = null)
    {
        ulong.TryParse(Request.Cookies[LastServerCookie], NumberStyles.None, Culture, out ulong lastUsed);
        lastUsed = guildId ?? lastUsed;
        var guild = lastUsed != 0 && await GetDiscordMemberAsync(lastUsed) is not null ? discord.GetGuild(lastUsed) : null;
        return View(new InfoPage(CommandHelp.Read(interactions), guild?.Id, guild?.Name));
    }
}
