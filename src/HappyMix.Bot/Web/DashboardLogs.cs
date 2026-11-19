using System.Globalization;
using System.Text;
using HappyMix.Application.Abstractions.Logs;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>The Logs tab: what HappyMix did in this server, for its administrators.</summary>
public sealed record LogsPage(ulong GuildId, string GuildName, LogQuery Query, IReadOnlyList<LogEntry> Entries, MemberNames Names,
    bool IsOwner, bool HasMore);

/// <summary>Filters as they appear in the address, so a filtered view can be shared or bookmarked.</summary>
public sealed record LogQuery(string Level, string? Area, string? Search, string Range, bool BotWide)
{
    public static readonly string[] Areas = ["commands", "discord", "jobs", "dashboard", "integrations", "system"];
    public static readonly string[] Ranges = ["1h", "24h", "7d", "all"];

    public LogSeverity Minimum => Level switch { "warning" => LogSeverity.Warning, "error" => LogSeverity.Error, "debug" => LogSeverity.Debug, _ => LogSeverity.Information };

    public DateTimeOffset? Since(DateTimeOffset now) => Range switch
    {
        "1h" => now.AddHours(-1), "7d" => now.AddDays(-7), "all" => null, _ => now.AddDays(-1)
    };

    public string QueryString(long? before = null, long? after = null)
    {
        var parts = new List<string> { "level=" + Level, "range=" + Range };
        if (Area is not null) parts.Add("area=" + Uri.EscapeDataString(Area));
        if (!string.IsNullOrWhiteSpace(Search)) parts.Add("q=" + Uri.EscapeDataString(Search));
        if (BotWide) parts.Add("botwide=1");
        if (before is { } b) parts.Add("before=" + b.ToString(CultureInfo.InvariantCulture));
        if (after is { } a) parts.Add("after=" + a.ToString(CultureInfo.InvariantCulture));
        return "?" + string.Join("&", parts);
    }
}

public sealed partial class DashboardController
{
    private const int LogPageSize = 100;

    [HttpGet("/logs/{guildId}")]
    public async Task<IActionResult> Logs(ulong guildId, string? level, string? area, string? q, string? range, bool botwide, long? before,
        [FromServices] ILogStore logs)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return await LogsForbiddenAsync(guildId);
        RememberServer(guildId);
        var (query, owner) = LogQueryFor(level, area, q, range, botwide);
        var entries = await logs.ReadAsync(Filter(guildId, query, owner, before, null, LogPageSize + 1), HttpContext.RequestAborted);
        ViewData["NavGuildName"] = guild.Name;
        return View("Logs", new LogsPage(guildId, guild.Name, query, entries.Take(LogPageSize).ToArray(), await NamesAsync(guildId), owner,
            entries.Count > LogPageSize));
    }

    /// <summary>Rows only: newer than <paramref name="after"/> for the live view, or older than <paramref name="before"/> for "Show older".</summary>
    [HttpGet("/logs/{guildId}/rows")]
    public async Task<IActionResult> LogRows(ulong guildId, string? level, string? area, string? q, string? range, bool botwide, long? before, long? after,
        [FromServices] ILogStore logs)
    {
        if (await GetAdministratorAsync(guildId) is null)
            return StatusCode(StatusCodes.Status403Forbidden);
        var (query, owner) = LogQueryFor(level, area, q, range, botwide);
        var entries = await logs.ReadAsync(Filter(guildId, query, owner, before, after, LogPageSize + 1), HttpContext.RequestAborted);
        Response.Headers["X-Has-More"] = entries.Count > LogPageSize ? "1" : "0";
        return PartialView("_LogRows", new LogsPage(guildId, discord.GetGuild(guildId)?.Name ?? "", query, entries.Take(LogPageSize).ToArray(),
            await NamesAsync(guildId), owner, entries.Count > LogPageSize));
    }

    /// <summary>The filtered entries as a CSV file (up to 5,000), for sharing a problem report.</summary>
    [HttpGet("/logs/{guildId}/export")]
    public async Task<IActionResult> ExportLogs(ulong guildId, string? level, string? area, string? q, string? range, bool botwide,
        [FromServices] ILogStore logs)
    {
        if (await GetAdministratorAsync(guildId) is null)
            return StatusCode(StatusCodes.Status403Forbidden);
        var (query, owner) = LogQueryFor(level, area, q, range, botwide);
        var csv = new StringBuilder("time_utc,level,area,server,user,message,exception\n");
        long? before = null;
        for (int page = 0; page < 10; page++)
        {
            var entries = await logs.ReadAsync(Filter(guildId, query, owner, before, null, 500), HttpContext.RequestAborted);
            foreach (var entry in entries)
                csv.Append(CultureInfo.InvariantCulture, $"{entry.At.UtcDateTime:yyyy-MM-dd HH:mm:ss},{entry.Level},{entry.Area},{entry.GuildId},{entry.UserId},")
                    .Append(Csv(entry.Message)).Append(',').Append(Csv(entry.Exception ?? "")).Append('\n');
            if (entries.Count < 500) break;
            before = entries[^1].Id;
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"happymix-logs-{guildId}-{clock.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private (LogQuery Query, bool Owner) LogQueryFor(string? level, string? area, string? search, string? range, bool botWide)
    {
        bool owner = IsLogOwner();
        var query = new LogQuery(level is "warning" or "error" or "debug" ? level : "info",
            LogQuery.Areas.Contains(area) ? area : null,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim()[..Math.Min(100, search.Trim().Length)],
            LogQuery.Ranges.Contains(range) ? range! : "24h",
            botWide && owner);
        return (query, owner);
    }

    private LogFilter Filter(ulong guildId, LogQuery query, bool owner, long? before, long? after, int limit) =>
        new(guildId, query.BotWide && owner, query.Minimum, query.Area, query.Search, query.Since(clock.UtcNow), before, after, limit);

    /// <summary>Bot owners (<c>Logs:OwnerIds</c>) also see bot-wide entries that belong to no server.</summary>
    private bool IsLogOwner() =>
        ulong.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
        && (HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetSection("Logs:OwnerIds").Get<ulong[]>() ?? []).Contains(id);

    private async Task<MemberNames> NamesAsync(ulong guildId)
    {
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, HttpContext.RequestAborted);
        return new MemberNames(members, available);
    }

    private async Task<IActionResult> LogsForbiddenAsync(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is null)
            return NotMember();
        return Forbidden(new ForbiddenPage(text["Logs_AdminsOnly"], text["Logs_AdminsOnlyText"], $"/availability/{guildId}", text["Logs_BackToAvailability"]));
    }

    /// <summary>A CSV field; a leading = + - @ is neutralised so spreadsheets don't run it as a formula.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
