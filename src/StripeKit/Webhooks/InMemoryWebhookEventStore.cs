using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class InMemoryWebhookEventStore : IWebhookEventStore
{
    private static readonly TimeSpan DefaultProcessingLeaseDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, WebhookEventRecord> _records = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _processingLeaseDuration;

    public InMemoryWebhookEventStore()
        : this(() => DateTimeOffset.UtcNow, DefaultProcessingLeaseDuration)
    {
    }

    public InMemoryWebhookEventStore(Func<DateTimeOffset> utcNow, TimeSpan processingLeaseDuration)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (processingLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processingLeaseDuration), "Processing lease duration must be greater than zero.");
        }

        _processingLeaseDuration = processingLeaseDuration;
    }

    public Task<bool> TryBeginAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        DateTimeOffset startedAt = _utcNow();
        bool started = false;
        _records.AddOrUpdate(
            eventId,
            _ =>
            {
                started = true;
                return WebhookEventRecord.CreateProcessing(eventId, startedAt);
            },
            (_, existing) =>
            {
                if (existing.State == WebhookEventState.Failed)
                {
                    started = true;
                    return WebhookEventRecord.CreateProcessing(eventId, startedAt);
                }

                if (existing.State == WebhookEventState.Processing &&
                    IsProcessingLeaseExpired(existing.StartedAtUtc, startedAt))
                {
                    started = true;
                    return WebhookEventRecord.CreateProcessing(eventId, startedAt);
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

        DateTimeOffset startedAt = _utcNow();
        _records.AddOrUpdate(
            eventId,
            _ => WebhookEventRecord.CreateCompleted(eventId, outcome, startedAt),
            (_, existing) => WebhookEventRecord.CreateCompleted(eventId, outcome, existing.StartedAtUtc));

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

    private bool IsProcessingLeaseExpired(DateTimeOffset startedAtUtc, DateTimeOffset nowUtc)
    {
        TimeSpan elapsed = nowUtc - startedAtUtc;
        return elapsed >= _processingLeaseDuration;
    }

    private sealed class WebhookEventRecord
    {
        public WebhookEventRecord(string eventId, DateTimeOffset startedAtUtc, WebhookEventState state, WebhookEventOutcome? outcome)
        {
            EventId = eventId;
            StartedAtUtc = startedAtUtc;
            State = state;
            Outcome = outcome;
        }

        public string EventId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public WebhookEventState State { get; }
        public WebhookEventOutcome? Outcome { get; }

        public static WebhookEventRecord CreateProcessing(string eventId, DateTimeOffset startedAtUtc)
        {
            return new WebhookEventRecord(eventId, startedAtUtc, WebhookEventState.Processing, null);
        }

        public static WebhookEventRecord CreateCompleted(string eventId, WebhookEventOutcome outcome, DateTimeOffset startedAtUtc)
        {
            WebhookEventState state = outcome.Succeeded
                ? WebhookEventState.Succeeded
                : WebhookEventState.Failed;

            return new WebhookEventRecord(eventId, startedAtUtc, state, outcome);
        }
    }

    private enum WebhookEventState
    {
        Processing,
        Succeeded,
        Failed
    }
}
