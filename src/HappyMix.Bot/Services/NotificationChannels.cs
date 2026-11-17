using Discord;

namespace HappyMix.Bot.Services;

/// <summary>Serializes category discovery and creation across notification features.</summary>
public sealed class NotificationChannels : IDisposable
{
    private readonly SemaphoreSlim _creation = new(1);

    public Task<ITextChannel> FindOrCreateAsync(IGuild guild, string name, string? topic = null) => CreateCoreAsync(guild, name, topic, true);
    public Task<ITextChannel> CreateAsync(IGuild guild, string name, string? topic = null) => CreateCoreAsync(guild, name, topic, false);

    private async Task<ITextChannel> CreateCoreAsync(IGuild guild, string name, string? topic, bool reuse)
    {
        await _creation.WaitAsync();
        try
        {
            if (reuse && (await guild.GetTextChannelsAsync()).FirstOrDefault(channel => channel.Name == name) is { } existing)
                return existing;
            var categories = await guild.GetCategoriesAsync();
            var category = categories.FirstOrDefault(item =>
                string.Equals(item.Name, "Bot Notifications", StringComparison.OrdinalIgnoreCase))
                ?? await guild.CreateCategoryAsync("Bot Notifications");
            return await guild.CreateTextChannelAsync(name, properties =>
            {
                properties.CategoryId = category.Id;
                if (topic is not null) properties.Topic = topic;
            });
        }
        finally
        {
            _creation.Release();
        }
    }

    public void Dispose() => _creation.Dispose();
}
