using System.Text.Json;
using Besitos.Examples;
using Besitos.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Besitos.Tests;

public sealed class OperationExampleTests
{
    [Fact]
    public async Task CounterHandlerDeduplicatesSeparateMessagesForOneBusinessOperation()
    {
        var file = Path.Combine(Path.GetTempPath(), $"besitos-example-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Pooling=False";
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var operations = new SqliteOperationExecutor();
            await operations.InitializeAsync(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE demo_counters(id TEXT PRIMARY KEY,value INTEGER NOT NULL); INSERT INTO demo_counters VALUES ('visits',0);";
            await command.ExecuteNonQueryAsync();
            var handler = new IncrementCounterHandler(() => new SqliteConnection(connectionString), operations);
            var payload = JsonSerializer.Serialize(new IncrementCounter(Guid.NewGuid(), "visits"));
            await handler.HandleAsync(Item(payload), default);
            await handler.HandleAsync(Item(payload), default);
            command.CommandText = "SELECT value FROM demo_counters WHERE id='visits'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            await handler.HandleAsync(Item(JsonSerializer.Serialize(new IncrementCounter(Guid.NewGuid(), "visits"))), default);
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ReceiptHandlerReusesProviderKeyAcrossSubmissions()
    {
        var gateway = new RecordingGateway();
        var handler = new SendReceiptHandler(gateway);
        var payload = JsonSerializer.Serialize(new SendReceipt(Guid.NewGuid(), "test@example.test", "Receipt"));
        await handler.HandleAsync(Item(payload), default);
        await handler.HandleAsync(Item(payload), default);
        Assert.Equal(2, gateway.Keys.Count);
        Assert.Equal(gateway.Keys[0], gateway.Keys[1]);
        await Assert.ThrowsAsync<PermanentCommandException>(() => handler.HandleAsync(Item("{broken"), default));
    }

    private static QueueItem Item(string payload)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "examples", "example.v1", payload, "application/json", null,
            QueueState.Leased, now, now, 1, 5, Guid.NewGuid(), now.AddMinutes(1), null);
    }

    private sealed class RecordingGateway : IIdempotentReceiptGateway
    {
        public List<string> Keys { get; } = [];
        public Task SendAsync(SendReceipt receipt, string idempotencyKey, CancellationToken cancellationToken)
        {
            Keys.Add(idempotencyKey);
            return Task.CompletedTask;
        }
    }
}
