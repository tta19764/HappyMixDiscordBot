namespace HappyMix.Application.Abstractions.Guilds;

public sealed record GuildSettings(ulong GuildId, ulong ChannelId, string TimeZoneId,
    GuildEsportsPreferences? Preferences = null);

public interface IGuildSettingsStore
{
    Task<GuildSettings?> GetAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SaveAsync(GuildSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuildSettings>> ListAsync(CancellationToken cancellationToken);
    Task UpdateAsync(ulong guildId, string defaultTimeZone, Func<GuildSettings, GuildSettings> update,
        CancellationToken cancellationToken = default);
}
