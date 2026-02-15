using System;

namespace StripeKit;

public sealed class WebhookEventOutcome
{
    public WebhookEventOutcome(bool succeeded, string? errorMessage, DateTimeOffset recordedAt)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
        RecordedAt = recordedAt;
    }

    public bool Succeeded { get; }
    public string? ErrorMessage { get; }
    public DateTimeOffset RecordedAt { get; }
}
