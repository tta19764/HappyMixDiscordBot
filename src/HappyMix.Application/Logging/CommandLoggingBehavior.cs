using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using HappyMix.Domain.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace HappyMix.Application.Logging;

/// <summary>
/// Logs every command: which one, who sent it, in which server, whether it worked and how long it took. Queries are only
/// logged when they throw. Only names, ids and our own error messages are logged, never what people typed. Background
/// commands that run every minute log at Debug so they don't drown out people's actions.
/// </summary>
internal sealed partial class CommandLoggingBehavior<TRequest, TResponse>(ILogger<CommandLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly string Name = typeof(TRequest).Name;
    private static readonly bool IsCommand = Name.EndsWith("Command", StringComparison.Ordinal);
    private static readonly bool IsRoutine = Name is "DispatchEventMessagesCommand" or "SynchronizeMatchesCommand" or "PublishGuildMatchesCommand"
        or "CopyDiscordPollVoteCommand" or "SyncDiscordPollVotesCommand";

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var (guildId, userId) = RequestIds.Read(request);
        long started = Stopwatch.GetTimestamp();
        try
        {
            var response = await next(cancellationToken);
            long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!IsCommand)
                return response;
            if (response is Result { IsFailure: true } failed)
                LogRejected(logger, Name, guildId, userId, failed.Error.Code, failed.Error.Description, elapsed);
            else if (IsRoutine)
                LogRoutine(logger, Name, guildId, elapsed);
            else
                LogSucceeded(logger, Name, guildId, userId, elapsed);
            return response;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailed(logger, Name, guildId, userId, error);
            throw;
        }
    }

    [LoggerMessage(EventId = 1, EventName = "CommandSucceeded", Level = LogLevel.Information,
        Message = "{Command} by user {UserId} in server {GuildId} succeeded ({ElapsedMs} ms)")]
    private static partial void LogSucceeded(ILogger logger, string command, ulong? guildId, ulong? userId, long elapsedMs);

    [LoggerMessage(EventId = 2, EventName = "CommandRejected", Level = LogLevel.Warning,
        Message = "{Command} by user {UserId} in server {GuildId} was rejected: {ErrorCode} {Error} ({ElapsedMs} ms)")]
    private static partial void LogRejected(ILogger logger, string command, ulong? guildId, ulong? userId, string errorCode, string error, long elapsedMs);

    [LoggerMessage(EventId = 3, EventName = "CommandFailed", Level = LogLevel.Error,
        Message = "{Command} by user {UserId} in server {GuildId} failed with an error")]
    private static partial void LogFailed(ILogger logger, string command, ulong? guildId, ulong? userId, Exception error);

    [LoggerMessage(EventId = 4, EventName = "RoutineCommand", Level = LogLevel.Debug, Message = "{Command} in server {GuildId} ran ({ElapsedMs} ms)")]
    private static partial void LogRoutine(ILogger logger, string command, ulong? guildId, long elapsedMs);
}

/// <summary>
/// Finds the server and user of a request: <c>GuildId</c> and <c>UserId</c> properties, directly or on a nested object
/// such as a <c>GroupActor</c> (Actor) or <c>GuildSettings</c> (Destination).
/// </summary>
internal static class RequestIds
{
    private static readonly ConcurrentDictionary<Type, Func<object, (ulong? Guild, ulong? User)>> Readers = new();

    public static (ulong? Guild, ulong? User) Read(object request) => Readers.GetOrAdd(request.GetType(), Build)(request);

    private static Func<object, (ulong?, ulong?)> Build(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var guild = properties.FirstOrDefault(property => property.Name == "GuildId");
        var user = properties.FirstOrDefault(property => property.Name == "UserId");
        var nested = properties.Where(property => property.PropertyType.IsClass && property.PropertyType != typeof(string))
            .Select(property => (Property: property,
                Guild: property.PropertyType.GetProperty("GuildId"), User: property.PropertyType.GetProperty("UserId")))
            .Where(item => item.Guild is not null || item.User is not null).ToArray();
        return request =>
        {
            ulong? guildId = Value(guild, request), userId = Value(user, request);
            foreach (var (property, nestedGuild, nestedUser) in nested)
            {
                if (property.GetValue(request) is not { } inner) continue;
                guildId ??= Value(nestedGuild, inner);
                userId ??= Value(nestedUser, inner);
            }
            return (guildId, userId);
        };
    }

    private static ulong? Value(PropertyInfo? property, object target) => property?.GetValue(target) switch
    {
        ulong value when value != 0 => value,
        _ => null
    };
}
