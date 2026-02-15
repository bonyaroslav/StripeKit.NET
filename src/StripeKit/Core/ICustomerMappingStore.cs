using System.Threading.Tasks;

namespace StripeKit;

public interface ICustomerMappingStore
{
    Task SaveMappingAsync(string userId, string customerId);
    Task<string?> GetCustomerIdAsync(string userId);
    Task<string?> GetUserIdAsync(string customerId);
}
