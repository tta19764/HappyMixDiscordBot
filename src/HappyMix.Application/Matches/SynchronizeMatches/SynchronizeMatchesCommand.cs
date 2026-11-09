using HappyMix.Application.Abstractions.Messaging;

namespace HappyMix.Application.Matches.SynchronizeMatches;

/// <summary>
/// Requests synchronization of followed CS2/Dota 2 main-team series and announcement state.
/// </summary>
public sealed record SynchronizeMatchesCommand : ICommand;
