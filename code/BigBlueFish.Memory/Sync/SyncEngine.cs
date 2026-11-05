namespace BigBlueFish.Memory.Sync;

public sealed class SyncEngine(ISyncStore store)
{
    public IReadOnlyList<SyncResult> Push(Guid accountId, Guid deviceId, Guid profileId, long epoch, IReadOnlyList<SyncOperation> operations)
    {
        store.EnsureProfile(accountId, profileId);
        if (epoch != store.Epoch(accountId, profileId))
            return operations.Select(op => new SyncResult(op.OperationId, SyncStatus.EpochRejected, null, null)).ToArray();
        return operations.Select(op => store.Apply(accountId, deviceId, profileId, epoch, op)).ToArray();
    }
}
