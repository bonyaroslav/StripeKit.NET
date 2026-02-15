namespace StripeKit.Tests;

public class InMemoryCustomerMappingStoreTests
{
    [Fact]
    public async Task SaveAndGetByUserId_ReturnsCustomerId()
    {
        InMemoryCustomerMappingStore store = new InMemoryCustomerMappingStore();

        await store.SaveMappingAsync("user_1", "cus_1");

        string? customerId = await store.GetCustomerIdAsync("user_1");

        Assert.Equal("cus_1", customerId);
    }

    [Fact]
    public async Task SaveAndGetByCustomerId_ReturnsUserId()
    {
        InMemoryCustomerMappingStore store = new InMemoryCustomerMappingStore();

        await store.SaveMappingAsync("user_2", "cus_2");

        string? userId = await store.GetUserIdAsync("cus_2");

        Assert.Equal("user_2", userId);
    }

    [Fact]
    public async Task SaveMappingAsync_EmptyUserId_ThrowsArgumentException()
    {
        InMemoryCustomerMappingStore store = new InMemoryCustomerMappingStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMappingAsync("", "cus_3"));
    }

    [Fact]
    public async Task GetCustomerIdAsync_EmptyUserId_ThrowsArgumentException()
    {
        InMemoryCustomerMappingStore store = new InMemoryCustomerMappingStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetCustomerIdAsync(""));
    }

    [Fact]
    public async Task GetUserIdAsync_EmptyCustomerId_ThrowsArgumentException()
    {
        InMemoryCustomerMappingStore store = new InMemoryCustomerMappingStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetUserIdAsync(""));
    }
}
