using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Quartz;

namespace HappyMix.Bot.Jobs;

[DisallowConcurrentExecution]
public sealed class PublishReleaseNotesJob(DiscordSocketClient discord, IConfiguration config,
    IWebHostEnvironment environment, ILogger<PublishReleaseNotesJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected) return;
        string directory = Path.GetDirectoryName(Path.GetFullPath(config["Storage:DatabasePath"] ?? "data/happymix.db"))!;
        string ready = Path.Combine(directory, "ready-release.txt");
        if (!File.Exists(ready)) return;
        string version = (await File.ReadAllTextAsync(ready, context.CancellationToken)).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d+\.\d+\.\d+$")) return;
        string manifest = Path.Combine(environment.ContentRootPath, "Releases", version + ".json");
        if (!File.Exists(manifest)) return;
        ulong guildId = config.GetValue<ulong>("ReleaseAnnouncements:GuildId");
        var guild = guildId == 0 ? discord.Guilds.Count == 1 ? discord.Guilds.Single() : null : discord.GetGuild(guildId);
        if (guild is null) return;
        string language = config["ReleaseAnnouncements:Language"] ?? "en";
        if (language is not ("en" or "uk" or "ru")) language = "en";
        var notes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(await File.ReadAllTextAsync(manifest, context.CancellationToken))!;
        string stateFile = Path.Combine(directory, $"release-messages-{guild.Id}.json");
        var state = File.Exists(stateFile)
            ? JsonSerializer.Deserialize<Dictionary<string, ulong>>(await File.ReadAllTextAsync(stateFile, context.CancellationToken))!
            : new Dictionary<string, ulong>(StringComparer.Ordinal);
        ulong channelId = config.GetValue<ulong>("ReleaseAnnouncements:ChannelId");
        if (channelId == 0) state.TryGetValue("channel", out channelId);
        var channels = await ((IGuild)guild).GetTextChannelsAsync();
        var channel = channels.FirstOrDefault(item => item.Id == channelId)
            ?? (channelId == 0 ? channels.FirstOrDefault(item => item.Name == "bot-releases") : null);
        channel ??= await guild.CreateTextChannelAsync("bot-releases");
        state["channel"] = channel.Id;
        await SaveAsync(stateFile, state, context.CancellationToken);
        var messages = await channel.GetMessagesAsync(100).FlattenAsync();
        foreach (var (body, index) in notes[language].Select((body, index) => (body, index)))
        {
            string key = $"{version}/{index}";
            if (state.ContainsKey(key)) continue;
            string marker = $"HappyMix {version} · {index + 1}/{notes[language].Length}";
            var existing = messages.FirstOrDefault(message => message.Author.Id == discord.CurrentUser.Id &&
                message.Embeds.Any(embed => embed.Footer?.Text == marker));
            if (existing is null)
            {
                var embed = new EmbedBuilder().WithTitle($"HappyMix {version}").WithDescription(body)
                    .WithFooter(marker).WithColor(Color.Blue).Build();
                existing = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { CancelToken = context.CancellationToken });
            }
            state[key] = existing.Id;
            await SaveAsync(stateFile, state, context.CancellationToken);
        }
        LogPublished(logger, version, guild.Id);
    }

    private static async Task SaveAsync(string path, Dictionary<string, ulong> state, CancellationToken ct)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), ct);
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly Action<ILogger, string, ulong, Exception?> Published = LoggerMessage.Define<string, ulong>(
        LogLevel.Information, new EventId(1, "ReleasePublished"), "Release {Version} published to guild {GuildId}");
    private static void LogPublished(ILogger logger, string version, ulong guildId) => Published(logger, version, guildId, null);
}
