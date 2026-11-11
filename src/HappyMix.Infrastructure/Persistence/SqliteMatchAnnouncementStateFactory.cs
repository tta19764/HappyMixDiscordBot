using System.Globalization;
using HappyMix.Application.Abstractions.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteMatchAnnouncementStateFactory(
    IDbContextFactory<HappyMixDbContext> factory, IOptions<StorageOptions> options) : IMatchAnnouncementStateFactory
{
    public IMatchAnnouncementState ForChannel(ulong channelId) =>
        new SqliteMatchAnnouncementState(factory, options, channelId.ToString(CultureInfo.InvariantCulture));
}
