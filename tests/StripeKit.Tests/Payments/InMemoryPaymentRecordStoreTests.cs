namespace StripeKit.Tests;

public class InMemoryPaymentRecordStoreTests
{
    [Fact]
    public async Task SaveAndGetByBusinessId_ReturnsRecord()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();
        PaymentRecord record = new PaymentRecord("user_1", "pay_1", PaymentStatus.Pending, "pi_1", null);

        await store.SaveAsync(record);

        PaymentRecord? loaded = await store.GetByBusinessIdAsync("pay_1");

        Assert.NotNull(loaded);
        Assert.Equal("user_1", loaded!.UserId);
        Assert.Equal("pi_1", loaded.PaymentIntentId);
    }

    [Fact]
    public async Task SaveAndGetByPaymentIntentId_ReturnsRecord()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();
        PaymentRecord record = new PaymentRecord("user_2", "pay_2", PaymentStatus.Pending, "pi_2", "ch_2");

        await store.SaveAsync(record);

        PaymentRecord? loaded = await store.GetByPaymentIntentIdAsync("pi_2");

        Assert.NotNull(loaded);
        Assert.Equal("pay_2", loaded!.BusinessPaymentId);
        Assert.Equal("ch_2", loaded.ChargeId);
    }

    [Fact]
    public async Task SaveAsync_NewPaymentIntentId_ReplacesLookup()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();
        PaymentRecord first = new PaymentRecord("user_3", "pay_3", PaymentStatus.Pending, "pi_old", null);
        PaymentRecord second = new PaymentRecord("user_3", "pay_3", PaymentStatus.Succeeded, "pi_new", null);

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        PaymentRecord? oldLookup = await store.GetByPaymentIntentIdAsync("pi_old");
        PaymentRecord? newLookup = await store.GetByPaymentIntentIdAsync("pi_new");

        Assert.Null(oldLookup);
        Assert.NotNull(newLookup);
        Assert.Equal(PaymentStatus.Succeeded, newLookup!.Status);
    }

    [Fact]
    public async Task GetByBusinessIdAsync_EmptyBusinessId_ThrowsArgumentException()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetByBusinessIdAsync(""));
    }

    [Fact]
    public async Task GetByPaymentIntentIdAsync_EmptyPaymentIntentId_ThrowsArgumentException()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetByPaymentIntentIdAsync(""));
    }

    [Fact]
    public async Task SaveAsync_NullRecord_ThrowsArgumentNullException()
    {
        IPaymentRecordStore store = new InMemoryPaymentRecordStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }
}
