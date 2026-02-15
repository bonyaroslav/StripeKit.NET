using System.Data.Common;
using StripeKit;

namespace StripeKit.SampleApi.SampleStorage;

public sealed class DbStripeKitStore : ICustomerMappingStore, IWebhookEventStore, IPaymentRecordStore, ISubscriptionRecordStore, IRefundRecordStore
{
    private readonly Func<DbConnection> _connectionFactory;

    public DbStripeKitStore(Func<DbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task SaveMappingAsync(string userId, string customerId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer ID is required.", nameof(customerId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        bool exists = await ExistsAsync(
            connection,
            "select 1 from customer_mappings where user_id = @user_id",
            ("@user_id", userId)).ConfigureAwait(false);

        if (exists)
        {
            await ExecuteNonQueryAsync(
                connection,
                "update customer_mappings set customer_id = @customer_id where user_id = @user_id",
                ("@customer_id", customerId),
                ("@user_id", userId)).ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            "insert into customer_mappings (user_id, customer_id) values (@user_id, @customer_id)",
            ("@user_id", userId),
            ("@customer_id", customerId)).ConfigureAwait(false);
    }

    public async Task<string?> GetCustomerIdAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        return await ExecuteScalarStringAsync(
            connection,
            "select customer_id from customer_mappings where user_id = @user_id",
            ("@user_id", userId)).ConfigureAwait(false);
    }

    public async Task<string?> GetUserIdAsync(string customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer ID is required.", nameof(customerId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        return await ExecuteScalarStringAsync(
            connection,
            "select user_id from customer_mappings where customer_id = @customer_id",
            ("@customer_id", customerId)).ConfigureAwait(false);
    }

    public async Task<bool> TryBeginAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(
                connection,
                "insert into webhook_events (event_id, started_at_utc) values (@event_id, @started_at_utc)",
                ("@event_id", eventId),
                ("@started_at_utc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public async Task RecordOutcomeAsync(string eventId, WebhookEventOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        if (outcome == null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            "update webhook_events set succeeded = @succeeded, error_message = @error_message, recorded_at_utc = @recorded_at_utc where event_id = @event_id",
            ("@succeeded", outcome.Succeeded ? 1 : 0),
            ("@error_message", outcome.ErrorMessage),
            ("@recorded_at_utc", outcome.RecordedAt.ToString("O")),
            ("@event_id", eventId)).ConfigureAwait(false);
    }

    public async Task<WebhookEventOutcome?> GetOutcomeAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        using DbCommand command = CreateCommand(
            connection,
            "select succeeded, error_message, recorded_at_utc from webhook_events where event_id = @event_id",
            ("@event_id", eventId));

        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        object succeededValue = reader.GetValue(0);
        bool succeeded = Convert.ToInt32(succeededValue) == 1;
        string? errorMessage = reader.IsDBNull(1) ? null : reader.GetString(1);
        string? recordedAtText = reader.IsDBNull(2) ? null : reader.GetString(2);
        DateTimeOffset recordedAt = ParseRecordedAt(recordedAtText);

        return new WebhookEventOutcome(succeeded, errorMessage, recordedAt);
    }

    public async Task SaveAsync(PaymentRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        bool exists = await ExistsAsync(
            connection,
            "select 1 from payment_records where business_payment_id = @business_payment_id",
            ("@business_payment_id", record.BusinessPaymentId)).ConfigureAwait(false);

        if (exists)
        {
            await ExecuteNonQueryAsync(
                connection,
                "update payment_records set user_id = @user_id, status = @status, payment_intent_id = @payment_intent_id, charge_id = @charge_id where business_payment_id = @business_payment_id",
                ("@user_id", record.UserId),
                ("@status", record.Status.ToString()),
                ("@payment_intent_id", record.PaymentIntentId),
                ("@charge_id", record.ChargeId),
                ("@business_payment_id", record.BusinessPaymentId)).ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            "insert into payment_records (business_payment_id, user_id, status, payment_intent_id, charge_id) values (@business_payment_id, @user_id, @status, @payment_intent_id, @charge_id)",
            ("@business_payment_id", record.BusinessPaymentId),
            ("@user_id", record.UserId),
            ("@status", record.Status.ToString()),
            ("@payment_intent_id", record.PaymentIntentId),
            ("@charge_id", record.ChargeId)).ConfigureAwait(false);
    }

    public async Task<PaymentRecord?> GetByBusinessIdAsync(string businessPaymentId)
    {
        if (string.IsNullOrWhiteSpace(businessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(businessPaymentId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        using DbCommand command = CreateCommand(
            connection,
            "select user_id, status, payment_intent_id, charge_id from payment_records where business_payment_id = @business_payment_id",
            ("@business_payment_id", businessPaymentId));

        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        string userId = reader.GetString(0);
        string statusText = reader.GetString(1);
        string? paymentIntentId = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? chargeId = reader.IsDBNull(3) ? null : reader.GetString(3);

        PaymentStatus status = ParsePaymentStatus(statusText);
        return new PaymentRecord(userId, businessPaymentId, status, paymentIntentId, chargeId);
    }

    public async Task<PaymentRecord?> GetByPaymentIntentIdAsync(string paymentIntentId)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
        {
            throw new ArgumentException("Payment intent ID is required.", nameof(paymentIntentId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        string? businessPaymentId = await ExecuteScalarStringAsync(
            connection,
            "select business_payment_id from payment_records where payment_intent_id = @payment_intent_id",
            ("@payment_intent_id", paymentIntentId)).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(businessPaymentId))
        {
            return null;
        }

        return await GetByBusinessIdAsync(businessPaymentId).ConfigureAwait(false);
    }

    public async Task SaveAsync(SubscriptionRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        bool exists = await ExistsAsync(
            connection,
            "select 1 from subscription_records where business_subscription_id = @business_subscription_id",
            ("@business_subscription_id", record.BusinessSubscriptionId)).ConfigureAwait(false);

        if (exists)
        {
            await ExecuteNonQueryAsync(
                connection,
                "update subscription_records set user_id = @user_id, status = @status, customer_id = @customer_id, subscription_id = @subscription_id where business_subscription_id = @business_subscription_id",
                ("@user_id", record.UserId),
                ("@status", record.Status.ToString()),
                ("@customer_id", record.CustomerId),
                ("@subscription_id", record.SubscriptionId),
                ("@business_subscription_id", record.BusinessSubscriptionId)).ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            "insert into subscription_records (business_subscription_id, user_id, status, customer_id, subscription_id) values (@business_subscription_id, @user_id, @status, @customer_id, @subscription_id)",
            ("@business_subscription_id", record.BusinessSubscriptionId),
            ("@user_id", record.UserId),
            ("@status", record.Status.ToString()),
            ("@customer_id", record.CustomerId),
            ("@subscription_id", record.SubscriptionId)).ConfigureAwait(false);
    }

    public async Task<SubscriptionRecord?> GetByBusinessIdAsync(string businessSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(businessSubscriptionId))
        {
            throw new ArgumentException("Business subscription ID is required.", nameof(businessSubscriptionId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        using DbCommand command = CreateCommand(
            connection,
            "select user_id, status, customer_id, subscription_id from subscription_records where business_subscription_id = @business_subscription_id",
            ("@business_subscription_id", businessSubscriptionId));

        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        string userId = reader.GetString(0);
        string statusText = reader.GetString(1);
        string? customerId = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? subscriptionId = reader.IsDBNull(3) ? null : reader.GetString(3);

        SubscriptionStatus status = ParseSubscriptionStatus(statusText);
        return new SubscriptionRecord(userId, businessSubscriptionId, status, customerId, subscriptionId);
    }

    public async Task<SubscriptionRecord?> GetBySubscriptionIdAsync(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription ID is required.", nameof(subscriptionId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        string? businessSubscriptionId = await ExecuteScalarStringAsync(
            connection,
            "select business_subscription_id from subscription_records where subscription_id = @subscription_id",
            ("@subscription_id", subscriptionId)).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(businessSubscriptionId))
        {
            return null;
        }

        return await GetByBusinessIdAsync(businessSubscriptionId).ConfigureAwait(false);
    }

    public async Task SaveAsync(RefundRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);

        bool exists = await ExistsAsync(
            connection,
            "select 1 from refund_records where business_refund_id = @business_refund_id",
            ("@business_refund_id", record.BusinessRefundId)).ConfigureAwait(false);

        if (exists)
        {
            await ExecuteNonQueryAsync(
                connection,
                "update refund_records set user_id = @user_id, business_payment_id = @business_payment_id, status = @status, payment_intent_id = @payment_intent_id, refund_id = @refund_id where business_refund_id = @business_refund_id",
                ("@user_id", record.UserId),
                ("@business_payment_id", record.BusinessPaymentId),
                ("@status", record.Status.ToString()),
                ("@payment_intent_id", record.PaymentIntentId),
                ("@refund_id", record.RefundId),
                ("@business_refund_id", record.BusinessRefundId)).ConfigureAwait(false);
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            "insert into refund_records (business_refund_id, user_id, business_payment_id, status, payment_intent_id, refund_id) values (@business_refund_id, @user_id, @business_payment_id, @status, @payment_intent_id, @refund_id)",
            ("@business_refund_id", record.BusinessRefundId),
            ("@user_id", record.UserId),
            ("@business_payment_id", record.BusinessPaymentId),
            ("@status", record.Status.ToString()),
            ("@payment_intent_id", record.PaymentIntentId),
            ("@refund_id", record.RefundId)).ConfigureAwait(false);
    }

    public async Task<RefundRecord?> GetByBusinessIdAsync(string businessRefundId)
    {
        if (string.IsNullOrWhiteSpace(businessRefundId))
        {
            throw new ArgumentException("Business refund ID is required.", nameof(businessRefundId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        using DbCommand command = CreateCommand(
            connection,
            "select user_id, business_payment_id, status, payment_intent_id, refund_id from refund_records where business_refund_id = @business_refund_id",
            ("@business_refund_id", businessRefundId));

        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        string userId = reader.GetString(0);
        string businessPaymentId = reader.GetString(1);
        string statusText = reader.GetString(2);
        string? paymentIntentId = reader.IsDBNull(3) ? null : reader.GetString(3);
        string? refundId = reader.IsDBNull(4) ? null : reader.GetString(4);

        RefundStatus status = ParseRefundStatus(statusText);
        return new RefundRecord(userId, businessRefundId, businessPaymentId, status, paymentIntentId, refundId);
    }

    public async Task<RefundRecord?> GetByRefundIdAsync(string refundId)
    {
        if (string.IsNullOrWhiteSpace(refundId))
        {
            throw new ArgumentException("Refund ID is required.", nameof(refundId));
        }

        using DbConnection connection = await OpenConnectionAsync().ConfigureAwait(false);
        string? businessRefundId = await ExecuteScalarStringAsync(
            connection,
            "select business_refund_id from refund_records where refund_id = @refund_id",
            ("@refund_id", refundId)).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(businessRefundId))
        {
            return null;
        }

        return await GetByBusinessIdAsync(businessRefundId).ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenConnectionAsync()
    {
        DbConnection connection = _connectionFactory();
        if (connection == null)
        {
            throw new InvalidOperationException("Connection factory returned null.");
        }

        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> ExistsAsync(
        DbConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        string? value = await ExecuteScalarStringAsync(connection, commandText, parameters).ConfigureAwait(false);
        return value != null;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        DbConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using DbCommand command = CreateCommand(connection, commandText, parameters);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        DbConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using DbCommand command = CreateCommand(connection, commandText, parameters);
        object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        return Convert.ToString(value);
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static DateTimeOffset ParseRecordedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.UtcNow;
        }

        if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private static PaymentStatus ParsePaymentStatus(string statusText)
    {
        if (Enum.TryParse(statusText, true, out PaymentStatus status))
        {
            return status;
        }

        return PaymentStatus.Pending;
    }

    private static SubscriptionStatus ParseSubscriptionStatus(string statusText)
    {
        if (Enum.TryParse(statusText, true, out SubscriptionStatus status))
        {
            return status;
        }

        return SubscriptionStatus.Incomplete;
    }

    private static RefundStatus ParseRefundStatus(string statusText)
    {
        if (Enum.TryParse(statusText, true, out RefundStatus status))
        {
            return status;
        }

        return RefundStatus.Pending;
    }
}
