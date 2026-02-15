using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class InMemoryWebhookEventStore : IWebhookEventStore
{
    // TODO: If needed, enforce "TryBeginAsync must succeed before RecordOutcomeAsync".
    private readonly ConcurrentDictionary<string, WebhookEventRecord> _records = new(StringComparer.Ordinal);

    public Task<bool> TryBeginAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        WebhookEventRecord record = new WebhookEventRecord(eventId);
        bool added = _records.TryAdd(eventId, record);

        return Task.FromResult(added);
    }

    public Task RecordOutcomeAsync(string eventId, WebhookEventOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        if (outcome == null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        _records.AddOrUpdate(
            eventId,
            _ => new WebhookEventRecord(eventId, outcome),
            (_, existing) => existing.WithOutcome(outcome));

        return Task.CompletedTask;
    }

    public Task<WebhookEventOutcome?> GetOutcomeAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        if (_records.TryGetValue(eventId, out WebhookEventRecord? record))
        {
            return Task.FromResult(record.Outcome);
        }

        return Task.FromResult<WebhookEventOutcome?>(null);
    }

    private sealed class WebhookEventRecord
    {
        public WebhookEventRecord(string eventId)
        {
            EventId = eventId;
        }

        public WebhookEventRecord(string eventId, WebhookEventOutcome outcome)
        {
            EventId = eventId;
            Outcome = outcome;
        }

        public string EventId { get; }
        public WebhookEventOutcome? Outcome { get; private set; }

        public WebhookEventRecord WithOutcome(WebhookEventOutcome outcome)
        {
            Outcome = outcome;
            return this;
        }
    }
}
