// Purpose: Reconcile Stripe state by replaying recent events through the webhook pipeline.
// Must-not-break: event.id dedupe is honored; handlers are identical to webhook processing.
// See: docs/plan.md and module README for invariants.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stripe;

namespace StripeKit;

public sealed class ReconciliationRequest
{
    public int? Limit { get; init; }
    public DateTimeOffset? CreatedAfter { get; init; }
    public string? StartingAfterEventId { get; init; }
}

public sealed class ReconciliationResult
{
    public ReconciliationResult(int total, int processed, int duplicates, int failed, string? lastEventId, bool hasMore)
    {
        Total = total;
        Processed = processed;
        Duplicates = duplicates;
        Failed = failed;
        LastEventId = lastEventId;
        HasMore = hasMore;
    }

    public int Total { get; }
    public int Processed { get; }
    public int Duplicates { get; }
    public int Failed { get; }
    public string? LastEventId { get; }
    public bool HasMore { get; }
}

public interface IStripeEventClient
{
    Task<StripeList<Event>> ListAsync(EventListOptions options, CancellationToken cancellationToken = default);
}

public sealed class StripeEventClient : IStripeEventClient
{
    private readonly EventService _eventService;

    public StripeEventClient(EventService eventService)
    {
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
    }

    public Task<StripeList<Event>> ListAsync(EventListOptions options, CancellationToken cancellationToken = default)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return _eventService.ListAsync(options, null, cancellationToken);
    }
}

public sealed class StripeEventReconciler
{
    private static readonly List<string> SupportedEventTypes = new List<string>
    {
        "payment_intent.succeeded",
        "payment_intent.payment_failed",
        "customer.subscription.created",
        "customer.subscription.updated",
        "customer.subscription.deleted",
        "invoice.payment_succeeded",
        "invoice.payment_failed",
        "refund.created",
        "refund.updated",
        "refund.failed"
    };

    private readonly IStripeEventClient _eventClient;
    private readonly StripeWebhookProcessor _processor;

    public StripeEventReconciler(IStripeEventClient eventClient, StripeWebhookProcessor processor)
    {
        _eventClient = eventClient ?? throw new ArgumentNullException(nameof(eventClient));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public async Task<ReconciliationResult> ReconcileAsync(
        ReconciliationRequest? request,
        CancellationToken cancellationToken = default)
    {
        int limit = request?.Limit ?? 100;
        if (limit <= 0 || limit > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 100.");
        }

        DateTimeOffset createdAfter = request?.CreatedAfter ?? DateTimeOffset.UtcNow.AddDays(-30);
        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.reconcile.run");
        activity?.SetTag("limit", limit);
        activity?.SetTag("created_after", createdAfter.ToString("O"));
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.StartingAfterEventId, request?.StartingAfterEventId);

        EventListOptions options = new EventListOptions
        {
            Limit = limit,
            Types = SupportedEventTypes,
            Created = new DateRangeOptions
            {
                GreaterThanOrEqual = createdAfter.UtcDateTime
            }
        };

        if (!string.IsNullOrWhiteSpace(request?.StartingAfterEventId))
        {
            options.StartingAfter = request.StartingAfterEventId;
        }

        StripeList<Event> events = await _eventClient.ListAsync(options, cancellationToken).ConfigureAwait(false);

        int total = 0;
        int processed = 0;
        int duplicates = 0;
        int failed = 0;
        string? lastEventId = null;

        foreach (Event stripeEvent in events.Data)
        {
            cancellationToken.ThrowIfCancellationRequested();

            total++;
            lastEventId = stripeEvent.Id;

            WebhookProcessingResult result = await _processor
                .ProcessStripeEventAsync(stripeEvent, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsDuplicate)
            {
                duplicates++;
            }
            else if (result.Outcome == null || !result.Outcome.Succeeded)
            {
                failed++;
            }
            else
            {
                processed++;
            }
        }

        activity?.SetTag("total", total);
        activity?.SetTag("processed", processed);
        activity?.SetTag("duplicates", duplicates);
        activity?.SetTag("failed", failed);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.LastEventId, lastEventId);
        activity?.SetTag("has_more", events.HasMore);

        return new ReconciliationResult(total, processed, duplicates, failed, lastEventId, events.HasMore);
    }
}
