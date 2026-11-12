using HappyMix.Domain.Esports;

namespace HappyMix.Bot.Services;

internal static class TwitchChannels
{
    internal static string? GetHandle(string value)
    {
        string handle = value.Trim();
        if (Uri.TryCreate(handle, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps ||
                (uri.Host != "twitch.tv" && uri.Host != "www.twitch.tv") ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
                return null;

            handle = uri.AbsolutePath.Trim('/');
        }

        return handle.Length is > 0 and <= 25 &&
            handle.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? handle.ToLowerInvariant()
            : null;
    }

    internal static string Format(EsportsMatch match, IReadOnlyList<string> preferredChannels)
    {
        var channels = preferredChannels.Select(GetHandle).OfType<string>()
            .Distinct(StringComparer.Ordinal).Take(5).ToArray();
        if (match.IsFinished || channels.Length == 0)
            return string.Empty;

        var matchChannels = (match.StreamUrls ?? []).Select(GetHandle).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var matching = channels.Where(matchChannels.Contains).ToArray();
        string label = matching.Length > 0 ? "Watch" : "Twitch channels";
        var links = (matching.Length > 0 ? matching : channels)
            .Select(handle => $"[{handle}](https://www.twitch.tv/{handle})");
        return $"\n{label}: {string.Join(" · ", links)}";
    }
}
