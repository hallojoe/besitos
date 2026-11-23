namespace Besitos.Storage;

internal sealed record BatchItem(Guid Id, Envelope Envelope, string Hash);

internal sealed record PreparedBatch(Guid[] Ids, BatchItem[] Items)
{
    public static PreparedBatch Create(IReadOnlyList<Envelope> envelopes, QueueLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        cancellationToken.ThrowIfCancellationRequested();
        if (envelopes.Count > limits.BatchItems) throw new ArgumentException("Batch exceeds the item limit.", nameof(envelopes));
        if (envelopes.Count == 0) return new(Array.Empty<Guid>(), Array.Empty<BatchItem>());
        var input = envelopes.ToArray();
        var ids = new Guid[input.Length];
        var unique = new Dictionary<Guid, BatchItem>();
        long bytes = 0;
        for (var i = 0; i < input.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = input[i];
            EnvelopeValidation.Validate(envelope, limits);
            var serialized = EnvelopeFingerprint.Serialize(envelope);
            bytes += serialized.Length;
            if (bytes > limits.BatchBytes) throw new ArgumentException("Batch exceeds the serialized byte limit.", nameof(envelopes));
            var hash = EnvelopeFingerprint.Compute(serialized);
            var id = envelope.Id ?? Guid.NewGuid();
            ids[i] = id;
            if (unique.TryGetValue(id, out var existing) && existing.Hash != hash)
                throw new InvalidOperationException("The batch repeats an ID with a different envelope.");
            unique.TryAdd(id, new(id, envelope, hash));
        }
        // Return IDs in input order; use stable insertion ordering to reduce lock inversions.
        return new(ids, unique.Values.OrderBy(item => item.Id).ToArray());
    }
}
