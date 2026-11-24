namespace Besitos.Email;

public enum EmailDeliveryState { Ready, Sending, Accepted, Rejected }
public enum EmailResolution { ConfirmedAccepted, ConfirmedNotAccepted }

public sealed record EmailDeliveryRecord(string RecipientKey, EmailDeliveryState State, string AttemptId, long UpdatedAt);
public sealed record EmailDeliveryClaim(bool Acquired, EmailDeliveryRecord Record);

/// <summary>
/// Implementations must be durable and atomic across processes. Sending reservations never expire automatically.
/// A lost write response must not grant ownership. Never remove records while replay remains possible.
/// </summary>
public interface IEmailDeliveryJournal
{
    /// <summary>Bind a business operation to immutable content and read its recipient states. Reject conflicting content.</summary>
    Task<IReadOnlyList<EmailDeliveryRecord>> PrepareAsync(OperationKey batch, string fingerprint, CancellationToken token = default);
    /// <summary>Atomically reserve a new or Ready recipient. Only the winner may send.</summary>
    Task<EmailDeliveryClaim> TryClaimAsync(OperationKey batch, OperationKey recipient, CancellationToken token = default);
    /// <summary>Change Sending only when the attempt ID matches. Return false if ownership no longer matches.</summary>
    Task<bool> CompleteAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailDeliveryState state, CancellationToken token = default);
    Task<IReadOnlyList<EmailDeliveryRecord>> ReadAsync(OperationKey batch, CancellationToken token = default);
    /// <summary>
    /// Requires external evidence and stopped/quiesced senders. ConfirmedNotAccepted permits another submission.
    /// This is an operator action, never an automatic timeout recovery mechanism.
    /// </summary>
    Task<bool> ResolveAsync(OperationKey batch, OperationKey recipient, string attemptId, EmailResolution resolution, CancellationToken token = default);
}
