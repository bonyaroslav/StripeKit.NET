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
    public async Task TryBeginAsync_FailedOutcome_AllowsRetry()
    {
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore();
        WebhookEventOutcome failed = new WebhookEventOutcome(false, "transient", DateTimeOffset.UtcNow);

        bool started = await store.TryBeginAsync("evt_retry_store");
        await store.RecordOutcomeAsync("evt_retry_store", failed);
        bool retried = await store.TryBeginAsync("evt_retry_store");
        WebhookEventOutcome? duringRetry = await store.GetOutcomeAsync("evt_retry_store");

        Assert.True(started);
        Assert.True(retried);
        Assert.Null(duringRetry);
    }

    [Fact]
    public async Task TryBeginAsync_StaleProcessingLease_AllowsTakeover()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-02-21T12:00:00Z");
        InMemoryWebhookEventStore store = new InMemoryWebhookEventStore(() => now, TimeSpan.FromMinutes(1));

        bool first = await store.TryBeginAsync("evt_stale_processing");
        now = now.AddSeconds(30);
        bool whileFresh = await store.TryBeginAsync("evt_stale_processing");
        now = now.AddMinutes(2);
        bool afterLease = await store.TryBeginAsync("evt_stale_processing");
        WebhookEventOutcome? outcome = await store.GetOutcomeAsync("evt_stale_processing");

        Assert.True(first);
        Assert.False(whileFresh);
        Assert.True(afterLease);
        Assert.Null(outcome);
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
