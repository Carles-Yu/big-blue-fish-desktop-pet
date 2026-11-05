namespace BigBlueFish.Memory.Sync;

public static class SyncRules
{
    public const int MaxOperations = 50;
    public const int PullPageSize = 200;
    public const int MaxCiphertextChars = 16 * 1024;
    public const int MaxRecords = 20_000;

    public static SyncStatus Decide(bool currentExists, bool currentDeleted, long currentRevision, SyncOperation operation)
    {
        if (operation.Kind == "delete") return SyncStatus.Accepted;
        if (currentExists && currentDeleted) return SyncStatus.Rejected;
        if (currentExists && currentRevision != operation.BaseRevision) return SyncStatus.Conflict;
        if (string.IsNullOrWhiteSpace(operation.Ciphertext) || string.IsNullOrWhiteSpace(operation.Nonce)) return SyncStatus.Rejected;
        if (operation.Ciphertext.Length > MaxCiphertextChars) return SyncStatus.Rejected;
        return SyncStatus.Accepted;
    }
}
