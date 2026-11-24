using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Besitos;

/// <summary>A stable, scoped identifier for one immutable business operation.</summary>
public sealed record OperationKey
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private OperationKey(string value) => Value = value;
    public string Value { get; }

    /// <summary>Identifies one command across claims and replay. Independent submissions need a business key.</summary>
    public static OperationKey ForMessage(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Create("message", item.Command, item.Id.ToString("N"));
    }

    /// <summary>Use a scope that identifies the operation, including its tenant when applicable.</summary>
    public static OperationKey ForBusiness(string scope, string operationId) => Create("business", scope, operationId);

    /// <summary>Identifies a stable sub-operation. Do not use an index if batch ordering can change.</summary>
    public OperationKey ForStep(string stepId) => Create("step", Value, stepId);

    public override string ToString() => Value;

    private static OperationKey Create(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(part);
            if (part.Length > 1024)
            {
                throw new ArgumentException("Operation key components must not exceed 1024 characters.");
            }

            var bytes = Utf8.GetBytes(part);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()));
    }
}
