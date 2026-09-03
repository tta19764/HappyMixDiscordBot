using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Status.GetBotStatus;

/// <summary>
/// Handles bot-status queries using the Application layer's clock abstraction.
/// </summary>
/// <param name="clock">Supplies the current UTC time without coupling the use case to the system clock.</param>
public sealed class GetBotStatusQueryHandler(IClock clock)
    : IQueryHandler<GetBotStatusQuery, BotStatus>
{
    /// <summary>
    /// Creates a successful status snapshot for the current instant.
    /// </summary>
    /// <param name="request">The status query.</param>
    /// <param name="cancellationToken">The token used to cancel request processing.</param>
    /// <returns>A successful result containing the current bot status.</returns>
    public Task<Result<BotStatus>> Handle(
        GetBotStatusQuery request,
        CancellationToken cancellationToken)
    {
        // There is no I/O in this handler today, so the result can be completed synchronously.
        // Keeping the MediatR Task contract allows future status checks to become asynchronous.
        return Task.FromResult(Result.Success(new BotStatus(clock.UtcNow)));
    }
}
