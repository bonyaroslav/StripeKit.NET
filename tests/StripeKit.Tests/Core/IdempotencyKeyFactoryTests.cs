namespace StripeKit.Tests;

public class IdempotencyKeyFactoryTests
{
    [Fact]
    public void Create_SameInputs_ReturnsSameKey()
    {
        string first = IdempotencyKeyFactory.Create("payment", "pay_123");
        string second = IdempotencyKeyFactory.Create("payment", "pay_123");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_DifferentBusinessId_ReturnsDifferentKey()
    {
        string first = IdempotencyKeyFactory.Create("payment", "pay_123");
        string second = IdempotencyKeyFactory.Create("payment", "pay_456");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Create_LongBusinessId_IsBoundedAndDeterministic()
    {
        string businessId = new string('a', 500);

        string first = IdempotencyKeyFactory.Create("payment", businessId);
        string second = IdempotencyKeyFactory.Create("payment", businessId);

        Assert.Equal(first, second);
        Assert.StartsWith("payment:", first);
        Assert.True(first.Length <= 255);
    }

    [Fact]
    public void Create_EmptyScope_Throws()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKeyFactory.Create("", "pay_123"));
    }

    [Fact]
    public void Create_EmptyBusinessId_Throws()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKeyFactory.Create("payment", ""));
    }
}
