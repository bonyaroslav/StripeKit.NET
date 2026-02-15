namespace StripeKit.Tests;

public class InMemorySubscriptionRecordStoreTests
{
    [Fact]
    public async Task SaveAndGetByBusinessId_ReturnsRecord()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();
        SubscriptionRecord record = new SubscriptionRecord("user_1", "sub_1", SubscriptionStatus.Incomplete, "cus_1", "sub_stripe_1");

        await store.SaveAsync(record);

        SubscriptionRecord? loaded = await store.GetByBusinessIdAsync("sub_1");

        Assert.NotNull(loaded);
        Assert.Equal("user_1", loaded!.UserId);
        Assert.Equal("cus_1", loaded.CustomerId);
    }

    [Fact]
    public async Task SaveAndGetBySubscriptionId_ReturnsRecord()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();
        SubscriptionRecord record = new SubscriptionRecord("user_2", "sub_2", SubscriptionStatus.Active, "cus_2", "sub_stripe_2");

        await store.SaveAsync(record);

        SubscriptionRecord? loaded = await store.GetBySubscriptionIdAsync("sub_stripe_2");

        Assert.NotNull(loaded);
        Assert.Equal("sub_2", loaded!.BusinessSubscriptionId);
        Assert.Equal(SubscriptionStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task SaveAsync_NewSubscriptionId_ReplacesLookup()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();
        SubscriptionRecord first = new SubscriptionRecord("user_3", "sub_3", SubscriptionStatus.Incomplete, "cus_3", "sub_old");
        SubscriptionRecord second = new SubscriptionRecord("user_3", "sub_3", SubscriptionStatus.Active, "cus_3", "sub_new");

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        SubscriptionRecord? oldLookup = await store.GetBySubscriptionIdAsync("sub_old");
        SubscriptionRecord? newLookup = await store.GetBySubscriptionIdAsync("sub_new");

        Assert.Null(oldLookup);
        Assert.NotNull(newLookup);
        Assert.Equal(SubscriptionStatus.Active, newLookup!.Status);
    }

    [Fact]
    public async Task GetByBusinessIdAsync_EmptyBusinessId_ThrowsArgumentException()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetByBusinessIdAsync(""));
    }

    [Fact]
    public async Task GetBySubscriptionIdAsync_EmptySubscriptionId_ThrowsArgumentException()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetBySubscriptionIdAsync(""));
    }

    [Fact]
    public async Task SaveAsync_NullRecord_ThrowsArgumentNullException()
    {
        ISubscriptionRecordStore store = new InMemorySubscriptionRecordStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }
}
