using System.Threading.Tasks;

namespace StripeKit;

public interface IWebhookEventStore
{
    Task<bool> TryBeginAsync(string eventId);
    Task RecordOutcomeAsync(string eventId, WebhookEventOutcome outcome);
    Task<WebhookEventOutcome?> GetOutcomeAsync(string eventId);
}
