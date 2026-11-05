namespace BigBlueFish.Memory.Sync;

public sealed class MemorySyncStore : ISyncStore
{
    sealed record Row(Guid RecordId, long Revision, long Epoch, string Ciphertext, string Nonce, bool Deleted, long Sequence);
    readonly Dictionary<(Guid Account, Guid Profile), long> epochs = new();
    readonly Dictionary<(Guid Account, Guid Profile), List<Row>> rows = new();
    readonly Dictionary<(Guid Device, Guid Operation), SyncResult> seen = new();
    readonly object gate = new();
    long sequence;

    public void EnsureProfile(Guid accountId, Guid profileId)
    {
        lock (gate)
        {
            epochs.TryAdd((accountId, profileId), 1);
            rows.TryAdd((accountId, profileId), []);
        }
    }

    public long Epoch(Guid accountId, Guid profileId) { lock (gate) return epochs[(accountId, profileId)]; }

    public SyncResult Apply(Guid accountId, Guid deviceId, Guid profileId, long epoch, SyncOperation operation)
    {
        lock (gate)
        {
            if (seen.TryGetValue((deviceId, operation.OperationId), out var previous)) return previous;
            var list = rows[(accountId, profileId)];
            var current = list.LastOrDefault(row => row.RecordId == operation.RecordId);
            var decision = SyncRules.Decide(current != null, current?.Deleted ?? false, current?.Revision ?? 0, operation);
            SyncResult result = decision switch
            {
                SyncStatus.Accepted => Append(accountId, profileId, operation, epoch, operation.Kind == "delete"),
                _ => new SyncResult(operation.OperationId, decision, current?.Revision, current?.Sequence)
            };
            seen[(deviceId, operation.OperationId)] = result;
            return result;
        }
    }

    static SyncResult With(SyncResult previous, SyncStatus status) => previous;

    public IReadOnlyList<SyncChange> Changes(Guid accountId, Guid profileId, long after, int limit)
    {
        lock (gate)
            return rows[(accountId, profileId)].Where(row => row.Sequence > after).OrderBy(row => row.Sequence).Take(limit)
                .Select(row => new SyncChange(row.Sequence, row.RecordId, row.Revision, row.Epoch, row.Ciphertext, row.Nonce, row.Deleted)).ToArray();
    }

    public long LatestSequence(Guid accountId, Guid profileId)
    {
        lock (gate) return rows[(accountId, profileId)].LastOrDefault()?.Sequence ?? 0;
    }

    public void Reset(Guid accountId, Guid profileId)
    {
        lock (gate)
        {
            epochs[(accountId, profileId)] = epochs.GetValueOrDefault((accountId, profileId), 1) + 1;
            rows[(accountId, profileId)] = [];
        }
    }

    SyncResult Append(Guid accountId, Guid profileId, SyncOperation operation, long epoch, bool deleted)
    {
        var seq = ++sequence;
        rows[(accountId, profileId)].Add(new Row(operation.RecordId, operation.Revision, epoch, operation.Ciphertext ?? "", operation.Nonce ?? "", deleted, seq));
        return new SyncResult(operation.OperationId, SyncStatus.Accepted, operation.Revision, seq);
    }

}
