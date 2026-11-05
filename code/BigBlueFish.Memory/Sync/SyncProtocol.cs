namespace BigBlueFish.Memory.Sync;

public enum SyncStatus { Accepted, Duplicate, Conflict, Rejected, EpochRejected }

public sealed record SyncOperation(Guid OperationId, Guid RecordId, long BaseRevision, string Kind, long Revision, string? Ciphertext, string? Nonce, bool Deleted);

public sealed record SyncResult(Guid OperationId, SyncStatus Status, long? ServerRevision, long? ServerSequence);

public sealed record SyncChange(long ServerSequence, Guid RecordId, long Revision, long Epoch, string Ciphertext, string Nonce, bool Deleted);

public interface ISyncStore
{
    void EnsureProfile(Guid accountId, Guid profileId);
    long Epoch(Guid accountId, Guid profileId);
    SyncResult Apply(Guid accountId, Guid deviceId, Guid profileId, long epoch, SyncOperation operation);
    IReadOnlyList<SyncChange> Changes(Guid accountId, Guid profileId, long after, int limit);
    long LatestSequence(Guid accountId, Guid profileId);
    void Reset(Guid accountId, Guid profileId);
}
