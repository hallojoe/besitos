using System.Data.Common;
using System.Text.Json;
using Besitos.Storage;

namespace Besitos.Examples;

// The host supplies an unopened business connection. Register the matching operation executor.
public sealed class IncrementCounterHandler(Func<DbConnection> createConnection, RelationalOperationExecutor operations) : JsonCommandHandler<IncrementCounter>
{
    protected override async Task HandleAsync(QueueItem item, IncrementCounter request, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CounterId) || request.CounterId.Length > 128)
        {
            throw new PermanentCommandException("Invalid counter operation.");
        }

        var key = OperationKey.ForBusiness("counter.increment.v1", request.OperationId.ToString("N"));
        await using var connection = createConnection();
        await connection.OpenAsync(cancellationToken);
        await operations.ExecuteAsync(connection, key, async (transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE demo_counters SET value=value+1 WHERE id=@id";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = request.CounterId;
            command.Parameters.Add(id);
            if (await command.ExecuteNonQueryAsync(token) != 1)
            {
                throw new PermanentCommandException("Counter does not exist.");
            }
        }, cancellationToken);
    }
}

public sealed record IncrementCounter(Guid OperationId, string CounterId);
public sealed record SendReceipt(Guid OperationId, string Recipient, string Text);

// An adapter must map the key to a provider's documented idempotency facility.
// Plain SMTP and MIME Message-ID do not implement this contract.
public interface IIdempotentReceiptGateway
{
    Task SendAsync(SendReceipt receipt, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class SendReceiptHandler(IIdempotentReceiptGateway gateway) : ICommandHandler
{
    public Task HandleAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var request = HandlerPayload.Parse<SendReceipt>(item.Payload);
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Recipient) || request.Text is null)
        {
            throw new PermanentCommandException("Invalid receipt operation.");
        }

        var key = OperationKey.ForBusiness("receipt.send.v1", request.OperationId.ToString("N"));
        return gateway.SendAsync(request, key.Value, cancellationToken);
    }
}

internal static class HandlerPayload
{
    public static T Parse<T>(string payload) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload) ?? throw new PermanentCommandException("Missing command payload."); }
        catch (JsonException) { throw new PermanentCommandException("Invalid command payload."); }
    }
}
