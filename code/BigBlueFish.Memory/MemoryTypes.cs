namespace BigBlueFish.Memory;

public enum MemoryKind { Profile, Event, Summary, Note }
public enum MemorySource { User, Candidate, Screen, Agent }
public enum SyncScope { LocalOnly, Profile }

public sealed record MemoryDraft(
    MemoryKind Kind,
    string Content,
    string[] Tags,
    MemorySource Source,
    string? SourceId,
    bool Confirmed,
    DateTimeOffset? ExpiresAt,
    SyncScope Scope,
    bool AllowCloudModel,
    bool Pinned);

public sealed record MemoryRecord(
    Guid Id,
    Guid ProfileId,
    MemoryKind Kind,
    string Content,
    string[] Tags,
    MemorySource Source,
    string? SourceId,
    bool Confirmed,
    DateTimeOffset? ExpiresAt,
    SyncScope Scope,
    bool AllowCloudModel,
    long Revision,
    long SyncedRevision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    bool Pinned);

public sealed record MemoryConflict(Guid RecordId, string LocalContent, string RemoteContent, long RemoteRevision);

public sealed record LocalMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);
