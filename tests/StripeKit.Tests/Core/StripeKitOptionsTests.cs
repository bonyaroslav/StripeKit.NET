namespace StripeKit.Tests;

public class StripeKitOptionsTests
{
    [Fact]
    public void Validate_PaymentsEnabledWebhooksDisabled_ThrowsInvalidOperationException()
    {
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = true,
            EnableBilling = false,
            EnableWebhooks = false
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_BillingEnabledWebhooksDisabled_ThrowsInvalidOperationException()
    {
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = false,
            EnableBilling = true,
            EnableWebhooks = false
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_OnlyCoreModulesEnabled_DoesNotThrow()
    {
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = false,
            EnableBilling = false,
            EnableWebhooks = false
        };

        options.Validate();
    }
}
