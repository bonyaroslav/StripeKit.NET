namespace StripeKit.Tests;

public class InMemoryRefundRecordStoreTests
{
    [Fact]
    public async Task SaveAsync_WhenRecordExists_UpdatesMapping()
    {
        IRefundRecordStore store = new InMemoryRefundRecordStore();

        RefundRecord initial = new RefundRecord(
            "user_1",
            "refund_1",
            "payment_1",
            RefundStatus.Pending,
            "pi_1",
            "re_1");
        await store.SaveAsync(initial);

        RefundRecord updated = new RefundRecord(
            "user_1",
            "refund_1",
            "payment_1",
            RefundStatus.Succeeded,
            "pi_1",
            "re_2");
        await store.SaveAsync(updated);

        RefundRecord? byBusiness = await store.GetByBusinessIdAsync("refund_1");
        RefundRecord? byOldRefund = await store.GetByRefundIdAsync("re_1");
        RefundRecord? byNewRefund = await store.GetByRefundIdAsync("re_2");

        Assert.NotNull(byBusiness);
        Assert.Null(byOldRefund);
        Assert.NotNull(byNewRefund);
        Assert.Equal(RefundStatus.Succeeded, byBusiness!.Status);
    }

    [Fact]
    public async Task GetByRefundIdAsync_UnknownRefund_ReturnsNull()
    {
        IRefundRecordStore store = new InMemoryRefundRecordStore();

        RefundRecord? record = await store.GetByRefundIdAsync("re_missing");

        Assert.Null(record);
    }
}