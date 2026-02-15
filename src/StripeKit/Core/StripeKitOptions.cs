namespace StripeKit;

public sealed class StripeKitOptions
{
    public bool EnablePayments { get; init; } = true;
    public bool EnableBilling { get; init; } = true;
    public bool EnablePromotions { get; init; } = true;
    public bool EnableRefunds { get; init; } = false;
    public bool EnableWebhooks { get; init; } = true;
}
