using System.Security.Cryptography;
using System.Text.Json;

namespace Besitos.Storage;

internal static class EnvelopeFingerprint
{
    // IDs identify rows. Fingerprints identify the original envelope, independent of queue state.
    public static byte[] Serialize(Envelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope with { Id = null });

    public static string Compute(Envelope envelope) => Compute(Serialize(envelope));

    public static string Compute(ReadOnlySpan<byte> serialized)
        => Convert.ToHexString(SHA256.HashData(serialized));
}
