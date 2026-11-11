using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Esports;

public interface IGuildMatchFeed
{
    Task<EsportsMatchDay> GetMatchesAsync(DateTimeOffset now, string timeZoneId,
        GuildEsportsPreferences preferences, CancellationToken cancellationToken);
}
