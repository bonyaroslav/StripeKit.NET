using System.Collections;
using System.Data;
using System.Data.Common;
using StripeKit.SampleApi.SampleStorage;

namespace StripeKit.Tests;

public class DbStripeKitStoreWebhookEventStoreTests
{
    [Fact]
    public async Task DbStripeKitStore_TryBeginAsync_NonUniqueDbException_Throws()
    {
        TestDbConnection connection = new TestDbConnection(commandText =>
        {
            throw new TestDbException("transient failure");
        });

        DbStripeKitStore store = new DbStripeKitStore(() => connection);

        await Assert.ThrowsAsync<TestDbException>(() => store.TryBeginAsync("evt_db_1"));
    }

    [Fact]
    public async Task DbStripeKitStore_GetOutcomeAsync_InProgressRowWithNullSucceeded_ReturnsNull()
    {
        DataTable table = new DataTable();
        table.Columns.Add("processing_state", typeof(string));
        table.Columns.Add("succeeded", typeof(object));
        table.Columns.Add("error_message", typeof(string));
        table.Columns.Add("recorded_at_utc", typeof(string));
        table.Rows.Add("processing", DBNull.Value, DBNull.Value, DBNull.Value);

        TestDbConnection connection = new TestDbConnection(_ => 1, _ => table.CreateDataReader());
        DbStripeKitStore store = new DbStripeKitStore(() => connection);

        WebhookEventOutcome? outcome = await store.GetOutcomeAsync("evt_db_2");

        Assert.Null(outcome);
    }

    [Fact]
    public async Task DbStripeKitStore_TryBeginAsync_StaleProcessingLease_AllowsTakeover()
    {
        int callCount = 0;
        string? updateCommand = null;
        DateTimeOffset now = DateTimeOffset.Parse("2026-02-21T12:00:00Z");

        TestDbConnection connection = new TestDbConnection(commandText =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new TestUniqueConstraintDbException("duplicate");
            }

            updateCommand = commandText;
            return 1;
        });

        DbStripeKitStore store = new DbStripeKitStore(() => connection, () => now, TimeSpan.FromMinutes(1));

        bool started = await store.TryBeginAsync("evt_db_stale");

        Assert.True(started);
        Assert.NotNull(updateCommand);
        Assert.Contains("processing_state = @existing_processing_state", updateCommand, StringComparison.Ordinal);
        Assert.Contains("started_at_utc <= @stale_before_utc", updateCommand, StringComparison.Ordinal);
    }

    private sealed class TestDbConnection : DbConnection
    {
        private readonly Func<string, int> _executeNonQuery;
        private readonly Func<string, DbDataReader>? _executeReader;
        private ConnectionState _state;

        public TestDbConnection(Func<string, int> executeNonQuery, Func<string, DbDataReader>? executeReader = null)
        {
            _executeNonQuery = executeNonQuery;
            _executeReader = executeReader;
        }

        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
            _state = ConnectionState.Closed;
        }

        public override void Open()
        {
            _state = ConnectionState.Open;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand()
        {
            return new TestDbCommand(this);
        }

        internal int ExecuteNonQuery(string commandText)
        {
            return _executeNonQuery(commandText);
        }

        internal DbDataReader ExecuteReader(string commandText)
        {
            if (_executeReader == null)
            {
                throw new NotSupportedException("Reader execution is not configured.");
            }

            return _executeReader(commandText);
        }
    }

    private sealed class TestDbCommand : DbCommand
    {
        private readonly TestDbConnection _connection;
        private readonly TestDbParameterCollection _parameters = new TestDbParameterCollection();

        public TestDbCommand(TestDbConnection connection)
        {
            _connection = connection;
        }

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection DbConnection
        {
            get => _connection;
            set => throw new NotSupportedException();
        }

        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            return _connection.ExecuteNonQuery(CommandText);
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecuteNonQuery());
        }

        public override object? ExecuteScalar()
        {
            return null;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<object?>(null);
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter()
        {
            return new TestDbParameter();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            return _connection.ExecuteReader(CommandText);
        }
    }

    private sealed class TestDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class TestDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new List<DbParameter>();

        public override int Count => _items.Count;
        public override object SyncRoot => this;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (object value in values)
            {
                Add(value);
            }
        }

        public override void Clear()
        {
            _items.Clear();
        }

        public override bool Contains(object value)
        {
            return _items.Contains((DbParameter)value);
        }

        public override bool Contains(string value)
        {
            return _items.Any(parameter => string.Equals(parameter.ParameterName, value, StringComparison.Ordinal));
        }

        public override void CopyTo(Array array, int index)
        {
            ((ICollection)_items).CopyTo(array, index);
        }

        public override IEnumerator GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        public override int IndexOf(object value)
        {
            return _items.IndexOf((DbParameter)value);
        }

        public override int IndexOf(string parameterName)
        {
            return _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));
        }

        public override void Insert(int index, object value)
        {
            _items.Insert(index, (DbParameter)value);
        }

        public override void Remove(object value)
        {
            _items.Remove((DbParameter)value);
        }

        public override void RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        public override void RemoveAt(string parameterName)
        {
            int index = IndexOf(parameterName);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        protected override DbParameter GetParameter(int index)
        {
            return _items[index];
        }

        protected override DbParameter GetParameter(string parameterName)
        {
            int index = IndexOf(parameterName);
            if (index < 0)
            {
                throw new IndexOutOfRangeException(parameterName);
            }

            return _items[index];
        }

        protected override void SetParameter(int index, DbParameter value)
        {
            _items[index] = value;
        }

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            int index = IndexOf(parameterName);
            if (index < 0)
            {
                _items.Add(value);
                return;
            }

            _items[index] = value;
        }
    }

    private class TestDbException : DbException
    {
        public TestDbException(string message)
            : base(message)
        {
        }
    }

    private sealed class TestUniqueConstraintDbException : TestDbException
    {
        public TestUniqueConstraintDbException(string message)
            : base(message)
        {
        }

        public int SqliteErrorCode => 19;
    }
}
