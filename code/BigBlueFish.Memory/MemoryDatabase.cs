using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BigBlueFish.Memory;

public sealed class MemoryDatabase : IDisposable
{
    readonly SqliteConnection connection;
    readonly object gate = new();
    SqliteTransaction? transaction;
    public Guid ProfileId { get; private set; }
    public bool SyncArmed { get; set; }

    MemoryDatabase(SqliteConnection connection, Guid profileId)
    {
        this.connection = connection;
        ProfileId = profileId;
    }

    public static MemoryDatabase Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "memory.db")}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS memory_item(
                  id TEXT PRIMARY KEY, profile_id TEXT NOT NULL, kind TEXT NOT NULL, content TEXT NOT NULL,
                  tags TEXT NOT NULL, source_type TEXT NOT NULL, source_id TEXT, confirmed INTEGER NOT NULL,
                  expires_at TEXT, sync_scope TEXT NOT NULL, allow_cloud INTEGER NOT NULL, revision INTEGER NOT NULL,
                  synced_revision INTEGER NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT, pinned INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox(
                  operation_id TEXT PRIMARY KEY, record_id TEXT NOT NULL, base_revision INTEGER NOT NULL,
                  op TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS conflict(
                  record_id TEXT PRIMARY KEY, local_content TEXT NOT NULL, remote_content TEXT NOT NULL, remote_revision INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS local_message(
                  id TEXT PRIMARY KEY, role TEXT NOT NULL, content TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS source_block(source_id TEXT PRIMARY KEY, reason TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS sync_state(profile_id TEXT PRIMARY KEY, cursor INTEGER NOT NULL, epoch INTEGER NOT NULL, device_id TEXT, last_success TEXT);
                """;
            command.ExecuteNonQuery();
        }
        var profile = ReadMeta(connection, "profile") is { } existing ? Guid.Parse(existing) : Guid.NewGuid();
        WriteMeta(connection, "profile", profile.ToString("D"));
        return new MemoryDatabase(connection, profile) { SyncArmed = ReadMeta(connection, "sync_armed") == "1" };
    }

    public void AdoptProfile(Guid profile)
    {
        if (profile == Guid.Empty) throw new InvalidOperationException("档案编号无效。");
        lock (gate)
        {
            if (profile == ProfileId) return;
            if (SyncCursor().Cursor != 0 || Outbox().Count != 0 || Query("SELECT * FROM memory_item WHERE synced_revision > 0").Count != 0)
                throw new InvalidOperationException("这台设备已有同步记录。请使用独立档案目录接入，不能直接覆盖原档案。");
            using var tx = Begin();
            Exec("UPDATE meta SET value=$p WHERE key='profile'", ("$p", profile.ToString("D")));
            Exec("UPDATE memory_item SET profile_id=$p", ("$p", profile.ToString("D")));
            Exec("DELETE FROM sync_state");
            tx.Inner.Commit();
            ProfileId = profile;
        }
    }

    public void ArmSync()
    {
        lock (gate)
        using (var tx = Begin())
        {
            SyncArmed = true;
            Exec("INSERT INTO meta(key,value) VALUES('sync_armed','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            foreach (var record in Query("SELECT * FROM memory_item WHERE sync_scope='Profile' AND confirmed=1 AND deleted_at IS NULL AND synced_revision=0 AND id NOT IN (SELECT record_id FROM outbox)"))
                Enqueue(record.Id, 0, "upsert", record.Scope);
            tx.Inner.Commit();
        }
    }

    public MemoryRecord Add(MemoryDraft draft) => Mutate(null, draft, false);
    public MemoryRecord Update(Guid id, MemoryDraft draft) => Mutate(id, draft, false);
    public void Delete(Guid id)
    {
        lock (gate)
        using (var tx = Begin())
        {
            var current = Find(id) ?? throw new InvalidOperationException("没有这条记忆");
            var revision = current.Revision + 1;
            var now = DateTimeOffset.UtcNow.ToString("O");
            Exec("UPDATE memory_item SET deleted_at=$d, revision=$r, updated_at=$u WHERE id=$id",
                ("$d", now), ("$r", revision), ("$u", now), ("$id", id.ToString("D")));
            Exec("DELETE FROM conflict WHERE record_id=$id", ("$id", id.ToString("D")));
            if (!string.IsNullOrWhiteSpace(current.SourceId))
                Exec("INSERT INTO source_block(source_id, reason) VALUES($s,'deleted') ON CONFLICT(source_id) DO UPDATE SET reason='deleted'", ("$s", current.SourceId));
            Enqueue(id, current.SyncedRevision, "delete", current.Scope);
            tx.Inner.Commit();
        }
    }

    public void Confirm(Guid id)
    {
        var current = Find(id) ?? throw new InvalidOperationException("没有这条记忆");
        Update(id, new MemoryDraft(current.Kind, current.Content, current.Tags, MemorySource.User, current.SourceId, true,
            current.ExpiresAt, current.Scope, current.AllowCloudModel, current.Pinned));
    }

    public IReadOnlyList<MemoryRecord> List() => Query("SELECT * FROM memory_item WHERE deleted_at IS NULL ORDER BY updated_at DESC");
    public IReadOnlyList<MemoryConflict> Conflicts()
    {
        lock (gate)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT record_id, local_content, remote_content, remote_revision FROM conflict";
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            var list = new List<MemoryConflict>();
            while (reader.Read())
                list.Add(new MemoryConflict(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            return list;
        }
    }

    public string Prompt(string scene, bool cloud, string kind)
    {
        var broad = kind is "companion" or "chat";
        var chosen = List()
            .Select(item => (item, score: MemoryText.Score(item, scene, kind)))
            .Where(x => x.score >= 0 && (!cloud || x.item.AllowCloudModel))
            .OrderByDescending(x => x.score)
            .Take(broad ? 8 : 3)
            .Select(x => x.item)
            .ToArray();
        return chosen.Length == 0 ? "" : MemoryText.FormatBlock(chosen, broad ? 500 : 180);
    }

    public IReadOnlyList<LocalMessage> RecentMessages(int count)
    {
        lock (gate)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, role, content, created_at FROM local_message ORDER BY created_at DESC LIMIT $n";
            command.Parameters.AddWithValue("$n", count);
            using var reader = command.ExecuteReader();
            var list = new List<LocalMessage>();
            while (reader.Read())
                list.Add(new LocalMessage(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            list.Reverse();
            return list;
        }
    }

    public void AddMessage(string role, string content)
    {
        lock (gate)
        {
            Exec("INSERT INTO local_message(id, role, content, created_at) VALUES($id,$role,$content,$at)",
                ("$id", Guid.NewGuid().ToString("D")), ("$role", role), ("$content", content), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            Exec("DELETE FROM local_message WHERE id NOT IN (SELECT id FROM local_message ORDER BY created_at DESC LIMIT 40)");
        }
    }

    public IReadOnlyList<(Guid OperationId, Guid RecordId, long BaseRevision, string Op)> Outbox()
    {
        lock (gate)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_id, record_id, base_revision, op FROM outbox ORDER BY created_at";
            using var reader = command.ExecuteReader();
            var list = new List<(Guid, Guid, long, string)>();
            while (reader.Read()) list.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3)));
            return list;
        }
    }

    public void Ack(Guid operationId, long syncedRevision, Guid? submittedRecordId = null)
    {
        lock (gate)
        using (var tx = Begin())
        {
            string? record = Scalar("SELECT record_id FROM outbox WHERE operation_id=$id", ("$id", operationId.ToString("D"))) as string;
            Exec("DELETE FROM outbox WHERE operation_id=$id", ("$id", operationId.ToString("D")));
            record ??= submittedRecordId?.ToString("D");
            if (record != null)
            {
                Exec("UPDATE memory_item SET synced_revision=max(synced_revision,$s) WHERE id=$id", ("$s", syncedRevision), ("$id", record));
                Exec("UPDATE outbox SET base_revision=max(base_revision,$s) WHERE record_id=$id", ("$s", syncedRevision), ("$id", record));
                Exec("DELETE FROM conflict WHERE record_id=$id", ("$id", record));
            }
            tx.Inner.Commit();
        }
    }

    public void DropOutbox(Guid operationId) { lock (gate) Exec("DELETE FROM outbox WHERE operation_id=$id", ("$id", operationId.ToString("D"))); }

    public (long Cursor, long Epoch) SyncCursor()
    {
        lock (gate)
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cursor, epoch FROM sync_state WHERE profile_id=$p";
            command.Parameters.AddWithValue("$p", ProfileId.ToString("D"));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 1);
        }
    }

    public void SetCursor(long cursor, long epoch)
    {
        lock (gate) Exec("""
            INSERT INTO sync_state(profile_id, cursor, epoch, last_success) VALUES($p,$c,$e,$t)
            ON CONFLICT(profile_id) DO UPDATE SET cursor=$c, epoch=$e, last_success=$t
            """, ("$p", ProfileId.ToString("D")), ("$c", cursor), ("$e", epoch), ("$t", DateTimeOffset.UtcNow.ToString("O")));
    }

    public void ApplyRemote(MemoryRecord remote)
    {
        lock (gate)
        using (var tx = Begin())
        {
            ApplyRemoteCore(remote);
            tx.Inner.Commit();
        }
    }

    public void ApplyRemotePage(IReadOnlyList<MemoryRecord> records, long cursor, long epoch)
    {
        lock (gate)
        using (var tx = Begin())
        {
            foreach (var record in records) ApplyRemoteCore(record);
            SetCursor(cursor, epoch);
            tx.Inner.Commit();
        }
    }

    void ApplyRemoteCore(MemoryRecord remote)
    {
            var current = Find(remote.Id);
            if (current != null && (current.Revision >= remote.Revision && current.SyncedRevision >= remote.Revision || current.DeletedAt != null && remote.DeletedAt == null)) return;
            var pending = Scalar("SELECT 1 FROM outbox WHERE record_id=$id", ("$id", remote.Id.ToString("D")));
            if (pending != null && remote.DeletedAt == null)
            {
                Exec("""
                    INSERT INTO conflict(record_id, local_content, remote_content, remote_revision) VALUES($id,$local,$remote,$rev)
                    ON CONFLICT(record_id) DO UPDATE SET remote_content=$remote, remote_revision=$rev
                    """, ("$id", remote.Id.ToString("D")), ("$local", Find(remote.Id)?.Content ?? ""), ("$remote", remote.Content), ("$rev", remote.Revision));
                return;
            }
            if (remote.DeletedAt != null)
            {
                Exec("DELETE FROM outbox WHERE record_id=$id", ("$id", remote.Id.ToString("D")));
                Exec("DELETE FROM conflict WHERE record_id=$id", ("$id", remote.Id.ToString("D")));
            }
            WriteRecord(remote);
    }

    public void ResolveConflict(Guid id, bool useCloudText)
    {
        lock (gate)
        using (var tx = Begin())
        {
            var conflict = Conflicts().FirstOrDefault(item => item.RecordId == id) ?? throw new InvalidOperationException("没有待处理的冲突。");
            var current = Find(id) ?? throw new InvalidOperationException("没有这条记忆。");
            var content = useCloudText ? conflict.RemoteContent : current.Content;
            var resolved = current with { Content = content, Revision = Math.Max(current.Revision, conflict.RemoteRevision) + 1,
                SyncedRevision = conflict.RemoteRevision, UpdatedAt = DateTimeOffset.UtcNow, Source = MemorySource.User };
            WriteRecord(resolved);
            Enqueue(id, conflict.RemoteRevision, "upsert", current.Scope);
            Exec("DELETE FROM conflict WHERE record_id=$id", ("$id", id.ToString("D")));
            tx.Inner.Commit();
        }
    }

    public void AcceptReset(long epoch)
    {
        lock (gate)
        using (var tx = Begin())
        {
            Exec("DELETE FROM outbox");
            Exec("DELETE FROM conflict");
            Exec("UPDATE memory_item SET deleted_at=$t,content='' WHERE sync_scope='Profile'", ("$t", DateTimeOffset.UtcNow.ToString("O")));
            SetCursor(0, epoch);
            tx.Inner.Commit();
        }
    }

    public IReadOnlyList<(Guid OperationId, Guid RecordId, long BaseRevision, string Op, MemoryRecord? Record)> PendingSnapshot()
    {
        lock (gate) return Outbox().Select(item => (item.OperationId, item.RecordId, item.BaseRevision, item.Op, Find(item.RecordId))).ToArray();
    }

    public bool SourceBlocked(string sourceId)
    {
        lock (gate) return Scalar("SELECT 1 FROM source_block WHERE source_id=$s", ("$s", sourceId)) != null;
    }

    public void Dispose() => connection.Dispose();

    Tx Begin()
    {
        var tx = new Tx(connection.BeginTransaction(), () => transaction = null);
        transaction = tx.Inner;
        return tx;
    }

    sealed class Tx(SqliteTransaction inner, Action done) : IDisposable
    {
        public SqliteTransaction Inner { get; } = inner;
        public void Dispose() { Inner.Dispose(); done(); }
    }

    MemoryRecord Mutate(Guid? id, MemoryDraft draft, bool _)
    {
        if (draft.Source is MemorySource.Screen or MemorySource.Agent)
            throw new InvalidOperationException("屏幕内容和外部 Agent 的话不能直接写成主人的记忆。");
        if (draft.Source == MemorySource.Candidate && draft.Confirmed)
            throw new InvalidOperationException("候选记忆需要主人确认后才能生效。");
        if (MemoryText.LooksSecret(draft.Content))
            throw new InvalidOperationException("这段内容像是密码或密钥，没有保存。");
        if (!string.IsNullOrWhiteSpace(draft.SourceId) && SourceBlocked(draft.SourceId))
            throw new InvalidOperationException("这个来源已经删除，不会自动再记一次。");
        lock (gate)
        using (var tx = Begin())
        {
            var current = id is { } existing ? Find(existing) : null;
            if (id != null && current == null) throw new InvalidOperationException("没有这条记忆");
            if (current is { Scope: SyncScope.Profile } && draft.Scope == SyncScope.LocalOnly)
            {
                WriteRecord(current with { Content = "", DeletedAt = DateTimeOffset.UtcNow, Revision = current.Revision + 1 });
                Enqueue(current.Id, current.SyncedRevision, "delete", current.Scope);
                // A fresh local ID lets the shared tombstone propagate without deleting the private copy.
                current = null;
                id = null;
            }
            var record = new MemoryRecord(id ?? Guid.NewGuid(), ProfileId, draft.Kind, draft.Content.Trim(), draft.Tags,
                draft.Source, draft.SourceId, draft.Confirmed, draft.ExpiresAt, draft.Scope, draft.Scope == SyncScope.LocalOnly ? false : draft.AllowCloudModel,
                (current?.Revision ?? 0) + 1, current?.SyncedRevision ?? 0, DateTimeOffset.UtcNow, null, draft.Pinned);
            WriteRecord(record);
            if (record.Confirmed) Enqueue(record.Id, current?.SyncedRevision ?? 0, "upsert", record.Scope);
            tx.Inner.Commit();
            return record;
        }
    }

    void WriteRecord(MemoryRecord record)
    {
        Exec("""
            INSERT INTO memory_item(id,profile_id,kind,content,tags,source_type,source_id,confirmed,expires_at,sync_scope,allow_cloud,revision,synced_revision,updated_at,deleted_at,pinned)
            VALUES($id,$p,$k,$c,$tags,$src,$sid,$ok,$exp,$scope,$cloud,$rev,$sync,$at,$del,$pin)
            ON CONFLICT(id) DO UPDATE SET kind=$k,content=$c,tags=$tags,source_type=$src,source_id=$sid,confirmed=$ok,expires_at=$exp,sync_scope=$scope,allow_cloud=$cloud,
              revision=$rev,synced_revision=$sync,updated_at=$at,deleted_at=$del,pinned=$pin
            """,
            ("$id", record.Id.ToString("D")), ("$p", record.ProfileId.ToString("D")), ("$k", record.Kind.ToString()), ("$c", record.Content),
            ("$tags", JsonSerializer.Serialize(record.Tags)), ("$src", record.Source.ToString()), ("$sid", (object?)record.SourceId ?? DBNull.Value),
            ("$ok", record.Confirmed ? 1 : 0), ("$exp", record.ExpiresAt?.ToString("O") ?? (object)DBNull.Value), ("$scope", record.Scope.ToString()),
            ("$cloud", record.AllowCloudModel ? 1 : 0), ("$rev", record.Revision), ("$sync", record.SyncedRevision), ("$at", record.UpdatedAt.ToString("O")),
            ("$del", record.DeletedAt?.ToString("O") ?? (object)DBNull.Value), ("$pin", record.Pinned ? 1 : 0));
    }

    void Enqueue(Guid id, long baseRevision, string op, SyncScope scope)
    {
        if (!SyncArmed || scope == SyncScope.LocalOnly) return;
        Exec("DELETE FROM outbox WHERE record_id=$id", ("$id", id.ToString("D")));
        Exec("INSERT INTO outbox(operation_id, record_id, base_revision, op, created_at) VALUES($op,$id,$base,$kind,$at)",
            ("$op", Guid.NewGuid().ToString("D")), ("$id", id.ToString("D")), ("$base", baseRevision), ("$kind", op), ("$at", DateTimeOffset.UtcNow.ToString("O")));
    }

    public MemoryRecord? FindAny(Guid id) => Find(id);
    MemoryRecord? Find(Guid id) => Query("SELECT * FROM memory_item WHERE id=$id", ("$id", id.ToString("D"))).FirstOrDefault();

    List<MemoryRecord> Query(string sql, params (string Name, object Value)[] parameters)
    {
        lock (gate)
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            using var reader = command.ExecuteReader();
            var list = new List<MemoryRecord>();
            while (reader.Read()) list.Add(Read(reader));
            return list;
        }
    }

    static MemoryRecord Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Enum.Parse<MemoryKind>(reader.GetString(2)), reader.GetString(3),
        JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [], Enum.Parse<MemorySource>(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7) == 1,
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        Enum.Parse<SyncScope>(reader.GetString(9)), reader.GetInt64(10) == 1, reader.GetInt64(11), reader.GetInt64(12),
        DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
        reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture), reader.GetInt64(15) == 1);

    void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteScalar();
    }

    static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key=$k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }
}
