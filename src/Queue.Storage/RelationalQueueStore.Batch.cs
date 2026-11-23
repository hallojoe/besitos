namespace Besitos.Storage;

public partial class RelationalQueueStore
{
    public Task<IReadOnlyList<Guid>> EnqueueBatchAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
    {
        // Prepare once, outside the retry loop, so generated IDs remain stable across attempts.
        var batch = PreparedBatch.Create(envelopes, _limits, cancellationToken);
        if (batch.Ids.Length == 0) return Task.FromResult<IReadOnlyList<Guid>>(batch.Ids);
        var inserter = new BatchInserter(dialect, _commands);
        return ExecuteAsync<IReadOnlyList<Guid>>(async connection =>
        {
            await using var transaction = await dialect.BeginBatchAsync(connection, cancellationToken);
            await inserter.InsertAsync(connection, transaction, batch.Items, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            return batch.Ids;
        }, cancellationToken);
    }
}
