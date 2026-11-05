using System.Security.Cryptography;
using System.Text;
using BigBlueFish.Memory.Sync;
using Npgsql;

namespace BigBlueFish.SyncServer;

public sealed class PostgresStore(NpgsqlDataSource dataSource) : ISyncStore
{
    public void Migrate(string sql)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public Guid Bootstrap()
    {
        using var connection = dataSource.OpenConnection();
        using var existing = new NpgsqlCommand("SELECT id FROM account ORDER BY created_at LIMIT 1", connection);
        if (existing.ExecuteScalar() is Guid found) return found;
        var id = Guid.NewGuid();
        using var insert = new NpgsqlCommand("INSERT INTO account(id, created_at) VALUES(@id, now())", connection);
        insert.Parameters.AddWithValue("id", id);
        insert.ExecuteNonQuery();
        return id;
    }

    public string CreatePairing(Guid accountId)
    {
        var code = NewCode();
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("INSERT INTO pairing(code_hash, account_id, expires_at) VALUES(@hash, @account, now() + interval '15 minutes')", connection);
        command.Parameters.AddWithValue("hash", Hash(code));
        command.Parameters.AddWithValue("account", accountId);
        command.ExecuteNonQuery();
        return code;
    }

    public (Guid AccountId, Guid DeviceId, string Token)? CompletePairing(string code, string deviceName)
    {
        using var connection = dataSource.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var find = new NpgsqlCommand("SELECT account_id FROM pairing WHERE code_hash=@hash AND consumed_at IS NULL AND expires_at > now() FOR UPDATE", connection, tx);
        find.Parameters.AddWithValue("hash", Hash(code.Trim()));
        if (find.ExecuteScalar() is not Guid account) return null;
        using var consume = new NpgsqlCommand("UPDATE pairing SET consumed_at=now() WHERE code_hash=@hash", connection, tx);
        consume.Parameters.AddWithValue("hash", Hash(code.Trim()));
        consume.ExecuteNonQuery();
        var device = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var insert = new NpgsqlCommand("INSERT INTO device(id, account_id, name, token_hash, created_at) VALUES(@id,@account,@name,@hash,now())", connection, tx);
        insert.Parameters.AddWithValue("id", device);
        insert.Parameters.AddWithValue("account", account);
        insert.Parameters.AddWithValue("name", deviceName.Length <= 40 ? deviceName : deviceName[..40]);
        insert.Parameters.AddWithValue("hash", Hash(token));
        insert.ExecuteNonQuery();
        tx.Commit();
        return (account, device, token);
    }

    public (Guid AccountId, Guid DeviceId)? Authenticate(string token)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT account_id, id FROM device WHERE token_hash=@hash AND revoked_at IS NULL", connection);
        command.Parameters.AddWithValue("hash", Hash(token));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    public IReadOnlyList<object> Devices(Guid accountId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT id, name, created_at, revoked_at FROM device WHERE account_id=@account ORDER BY created_at", connection);
        command.Parameters.AddWithValue("account", accountId);
        using var reader = command.ExecuteReader();
        var list = new List<object>();
        while (reader.Read())
            list.Add(new { id = reader.GetGuid(0), name = reader.GetString(1), createdAt = reader.GetDateTime(2), revoked = !reader.IsDBNull(3) });
        return list;
    }

    public bool Revoke(Guid accountId, Guid deviceId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("UPDATE device SET revoked_at=now() WHERE account_id=@account AND id=@id AND revoked_at IS NULL", connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("id", deviceId);
        return command.ExecuteNonQuery() == 1;
    }

    public void PublishEnvelope(Guid accountId, string transferCode, string wrapped)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            INSERT INTO envelope(code_hash, account_id, wrapped, expires_at) VALUES(@hash,@account,@wrapped, now() + interval '15 minutes')
            ON CONFLICT(code_hash) DO UPDATE SET wrapped=@wrapped, expires_at=now() + interval '15 minutes', consumed_at=NULL
            """, connection);
        command.Parameters.AddWithValue("hash", Hash(transferCode));
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("wrapped", wrapped);
        command.ExecuteNonQuery();
    }

    public string? RedeemEnvelope(Guid accountId, string transferCode)
    {
        using var connection = dataSource.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var find = new NpgsqlCommand("SELECT wrapped FROM envelope WHERE code_hash=@hash AND account_id=@account AND consumed_at IS NULL AND expires_at > now() FOR UPDATE", connection, tx);
        find.Parameters.AddWithValue("hash", Hash(transferCode));
        find.Parameters.AddWithValue("account", accountId);
        if (find.ExecuteScalar() is not string wrapped) return null;
        using var consume = new NpgsqlCommand("UPDATE envelope SET consumed_at=now() WHERE code_hash=@hash", connection, tx);
        consume.Parameters.AddWithValue("hash", Hash(transferCode));
        consume.ExecuteNonQuery();
        tx.Commit();
        return wrapped;
    }

    public void EnsureProfile(Guid accountId, Guid profileId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("INSERT INTO profile(account_id, profile_id, epoch) VALUES(@account,@profile,1) ON CONFLICT DO NOTHING", connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("profile", profileId);
        command.ExecuteNonQuery();
    }

    public long Epoch(Guid accountId, Guid profileId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT epoch FROM profile WHERE account_id=@account AND profile_id=@profile", connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("profile", profileId);
        return (long)(command.ExecuteScalar() ?? 1L);
    }

    public SyncResult Apply(Guid accountId, Guid deviceId, Guid profileId, long epoch, SyncOperation operation)
    {
        using var connection = dataSource.OpenConnection();
        using var tx = connection.BeginTransaction();
        // Serialize compare-and-write with other edits and resets of the same archive.
        using (var guard = new NpgsqlCommand("SELECT epoch FROM profile WHERE account_id=@account AND profile_id=@profile FOR UPDATE", connection, tx))
        {
            guard.Parameters.AddWithValue("account", accountId);
            guard.Parameters.AddWithValue("profile", profileId);
            if (guard.ExecuteScalar() is not long currentEpoch || currentEpoch != epoch)
                return new SyncResult(operation.OperationId, SyncStatus.EpochRejected, null, null);
        }
        using (var seen = new NpgsqlCommand("SELECT status, server_revision, server_seq FROM operation_log WHERE device_id=@device AND operation_id=@op", connection, tx))
        {
            seen.Parameters.AddWithValue("device", deviceId);
            seen.Parameters.AddWithValue("op", operation.OperationId);
            using var reader = seen.ExecuteReader();
            if (reader.Read())
                return new SyncResult(operation.OperationId, Enum.Parse<SyncStatus>(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }
        long currentRevision = 0;
        bool exists = false, deleted = false;
        long? currentSeq = null;
        using (var current = new NpgsqlCommand("""
            SELECT revision, deleted, server_seq FROM record_log
            WHERE account_id=@account AND profile_id=@profile AND record_id=@record
            ORDER BY server_seq DESC LIMIT 1
            """, connection, tx))
        {
            current.Parameters.AddWithValue("account", accountId);
            current.Parameters.AddWithValue("profile", profileId);
            current.Parameters.AddWithValue("record", operation.RecordId);
            using var reader = current.ExecuteReader();
            if (reader.Read()) { exists = true; currentRevision = reader.GetInt64(0); deleted = reader.GetBoolean(1); currentSeq = reader.GetInt64(2); }
        }
        var decision = SyncRules.Decide(exists, deleted, currentRevision, operation);
        long? seq = currentSeq;
        if (decision == SyncStatus.Accepted)
        {
            using var count = new NpgsqlCommand("SELECT count(DISTINCT record_id) FROM record_log WHERE account_id=@account AND profile_id=@profile", connection, tx);
            count.Parameters.AddWithValue("account", accountId);
            count.Parameters.AddWithValue("profile", profileId);
            if (!exists && Convert.ToInt32(count.ExecuteScalar()) >= SyncRules.MaxRecords)
                decision = SyncStatus.Rejected;
            else
            {
                using var insert = new NpgsqlCommand("""
                    INSERT INTO record_log(account_id, profile_id, record_id, revision, epoch, ciphertext, nonce, deleted)
                    VALUES(@account,@profile,@record,@revision,@epoch,@cipher,@nonce,@deleted) RETURNING server_seq
                    """, connection, tx);
                insert.Parameters.AddWithValue("account", accountId);
                insert.Parameters.AddWithValue("profile", profileId);
                insert.Parameters.AddWithValue("record", operation.RecordId);
                insert.Parameters.AddWithValue("revision", operation.Revision);
                insert.Parameters.AddWithValue("epoch", epoch);
                insert.Parameters.AddWithValue("cipher", operation.Ciphertext ?? "");
                insert.Parameters.AddWithValue("nonce", operation.Nonce ?? "");
                insert.Parameters.AddWithValue("deleted", operation.Kind == "delete");
                seq = (long)insert.ExecuteScalar()!;
            }
        }
        using var log = new NpgsqlCommand("INSERT INTO operation_log(device_id, operation_id, status, server_revision, server_seq) VALUES(@device,@op,@status,@revision,@seq)", connection, tx);
        log.Parameters.AddWithValue("device", deviceId);
        log.Parameters.AddWithValue("op", operation.OperationId);
        log.Parameters.AddWithValue("status", decision.ToString());
        log.Parameters.AddWithValue("revision", decision == SyncStatus.Accepted ? operation.Revision : currentRevision);
        log.Parameters.AddWithValue("seq", (object?)seq ?? DBNull.Value);
        log.ExecuteNonQuery();
        tx.Commit();
        return new SyncResult(operation.OperationId, decision, decision == SyncStatus.Accepted ? operation.Revision : currentRevision, seq);
    }

    public IReadOnlyList<SyncChange> Changes(Guid accountId, Guid profileId, long after, int limit)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            SELECT server_seq, record_id, revision, epoch, ciphertext, nonce, deleted FROM record_log
            WHERE account_id=@account AND profile_id=@profile AND server_seq > @after
            ORDER BY server_seq LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("profile", profileId);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<SyncChange>();
        while (reader.Read())
            list.Add(new SyncChange(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6)));
        return list;
    }

    public (long Epoch, IReadOnlyList<SyncChange> Changes) ReadPage(Guid accountId, Guid profileId, long after)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            SELECT p.epoch, r.server_seq, r.record_id, r.revision, r.ciphertext, r.nonce, r.deleted
            FROM profile p LEFT JOIN LATERAL (
              SELECT * FROM record_log WHERE account_id=p.account_id AND profile_id=p.profile_id
                AND epoch=p.epoch AND server_seq > @after ORDER BY server_seq LIMIT @limit
            ) r ON true WHERE p.account_id=@account AND p.profile_id=@profile ORDER BY r.server_seq
            """, connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("profile", profileId);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("limit", SyncRules.PullPageSize);
        using var reader = command.ExecuteReader();
        var list = new List<SyncChange>();
        long epoch = 1;
        while (reader.Read())
        {
            epoch = reader.GetInt64(0);
            if (!reader.IsDBNull(1)) list.Add(new(reader.GetInt64(1), reader.GetGuid(2), reader.GetInt64(3), epoch,
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6)));
        }
        return (epoch, list);
    }

    public long LatestSequence(Guid accountId, Guid profileId)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("SELECT COALESCE(max(server_seq),0) FROM record_log WHERE account_id=@account AND profile_id=@profile", connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("profile", profileId);
        return (long)command.ExecuteScalar()!;
    }

    public void Reset(Guid accountId, Guid profileId)
    {
        using var connection = dataSource.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var epoch = new NpgsqlCommand("UPDATE profile SET epoch=epoch+1 WHERE account_id=@account AND profile_id=@profile", connection, tx);
        epoch.Parameters.AddWithValue("account", accountId);
        epoch.Parameters.AddWithValue("profile", profileId);
        epoch.ExecuteNonQuery();
        using var clear = new NpgsqlCommand("DELETE FROM record_log WHERE account_id=@account AND profile_id=@profile", connection, tx);
        clear.Parameters.AddWithValue("account", accountId);
        clear.Parameters.AddWithValue("profile", profileId);
        clear.ExecuteNonQuery();
        tx.Commit();
    }

    static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(8, alphabet, (span, chars) =>
        {
            var bytes = RandomNumberGenerator.GetBytes(span.Length);
            for (int i = 0; i < span.Length; i++) span[i] = chars[bytes[i] % chars.Length];
        });
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
