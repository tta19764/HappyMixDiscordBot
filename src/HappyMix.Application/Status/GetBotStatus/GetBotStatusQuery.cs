using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain;

namespace HappyMix.Application.Status.GetBotStatus;

/// <summary>
/// Requests a snapshot of the bot's current application status.
/// </summary>
public sealed record GetBotStatusQuery : IQuery<BotStatus>;
