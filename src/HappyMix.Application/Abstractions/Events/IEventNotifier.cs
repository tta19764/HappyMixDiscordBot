namespace HappyMix.Application.Abstractions.Events;

public enum EventDeliveryOutcome { Sent, RetryLater, Undeliverable }

/// <summary>Sends one event message (invitation, confirmation or cancellation) to one person, usually by Discord DM.</summary>
public interface IEventNotifier
{
    Task<EventDeliveryOutcome> SendAsync(EventOutboxItem item, CancellationToken cancellationToken);
}
