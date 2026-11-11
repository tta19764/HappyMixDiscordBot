namespace HappyMix.Application.Abstractions.Guilds;

public sealed record GuildSettings(ulong GuildId, ulong ChannelId, string TimeZoneId);

public interface IGuildSettingsStore
{
    Task<GuildSettings?> GetAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveAsync(GuildSettings settings, CancellationToken cancellationToken = default);
}
