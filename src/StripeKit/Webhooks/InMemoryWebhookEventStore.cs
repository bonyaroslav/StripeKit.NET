using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class InMemoryWebhookEventStore : IWebhookEventStore
{
    private readonly ConcurrentDictionary<string, WebhookEventRecord> _records = new(StringComparer.Ordinal);

    public Task<bool> TryBeginAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        bool started = false;
        _records.AddOrUpdate(
            eventId,
            _ =>
            {
                started = true;
                return WebhookEventRecord.CreateProcessing(eventId);
            },
            (_, existing) =>
            {
                if (existing.State == WebhookEventState.Failed)
                {
                    started = true;
                    return WebhookEventRecord.CreateProcessing(eventId);
                }

                return existing;
            });

        return Task.FromResult(started);
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
            _ => WebhookEventRecord.CreateCompleted(eventId, outcome),
            (_, _) => WebhookEventRecord.CreateCompleted(eventId, outcome));

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
        public WebhookEventRecord(string eventId, WebhookEventState state, WebhookEventOutcome? outcome)
        {
            EventId = eventId;
            State = state;
            Outcome = outcome;
        }

        public string EventId { get; }
        public WebhookEventState State { get; }
        public WebhookEventOutcome? Outcome { get; }

        public static WebhookEventRecord CreateProcessing(string eventId)
        {
            return new WebhookEventRecord(eventId, WebhookEventState.Processing, null);
        }

        public static WebhookEventRecord CreateCompleted(string eventId, WebhookEventOutcome outcome)
        {
            WebhookEventState state = outcome.Succeeded
                ? WebhookEventState.Succeeded
                : WebhookEventState.Failed;

            return new WebhookEventRecord(eventId, state, outcome);
        }
    }

    private enum WebhookEventState
    {
        Processing,
        Succeeded,
        Failed
    }
}
