namespace StripeKit.Tests;

public class InMemoryWebhookEventStoreTests
{
    [Fact]
    public async Task TryBeginAsync_FirstTime_ReturnsTrue()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();

        bool started = await store.TryBeginAsync("evt_1");

        Assert.True(started);
    }

    [Fact]
    public async Task TryBeginAsync_Duplicate_ReturnsFalse()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();

        bool first = await store.TryBeginAsync("evt_2");
        bool second = await store.TryBeginAsync("evt_2");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task RecordOutcomeAsync_PersistsOutcome()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();
        WebhookEventOutcome outcome = new WebhookEventOutcome(true, null, DateTimeOffset.UtcNow);

        await store.RecordOutcomeAsync("evt_3", outcome);

        WebhookEventOutcome? saved = await store.GetOutcomeAsync("evt_3");

        Assert.NotNull(saved);
        Assert.True(saved!.Succeeded);
        Assert.Null(saved.ErrorMessage);
    }

    [Fact]
    public async Task TryBeginAsync_ThenRecordOutcome_AllowsReplayDetection()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();
        WebhookEventOutcome outcome = new WebhookEventOutcome(true, null, DateTimeOffset.UtcNow);

        bool started = await store.TryBeginAsync("evt_4");
        await store.RecordOutcomeAsync("evt_4", outcome);
        bool replay = await store.TryBeginAsync("evt_4");

        Assert.True(started);
        Assert.False(replay);
    }

    [Fact]
    public async Task TryBeginAsync_EmptyEventId_ThrowsArgumentException()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryBeginAsync(""));
    }

    [Fact]
    public async Task RecordOutcomeAsync_NullOutcome_ThrowsArgumentNullException()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RecordOutcomeAsync("evt_5", null!));
    }
}
