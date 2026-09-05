using HappyMix.Application.Abstractions.Messaging;

namespace HappyMix.Application.Matches.SynchronizeMatches;

/// <summary>
/// Requests synchronization of today's Counter-Strike matches and their announcement state.
/// </summary>
public sealed record SynchronizeMatchesCommand : ICommand;
