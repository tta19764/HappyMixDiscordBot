using Discord.Interactions;
using HappyMix.Bot.Interactions;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed partial class DashboardController
{
    [HttpGet("/info")]
    public IActionResult Info([FromServices] InteractionService interactions) =>
        View(CommandHelp.Read(interactions));
}
