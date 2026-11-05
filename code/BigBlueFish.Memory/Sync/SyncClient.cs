using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BigBlueFish.Memory.Sync;

public sealed class SyncClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SyncReport> Synchronize(MemoryDatabase database, byte[] archiveKey, Guid deviceId, string deviceToken, string serverUrl, HttpClient http, CancellationToken cancel)
    {
        var (cursor, epoch) = database.SyncCursor();
        var conflicts = database.Conflicts().Select(item => item.RecordId).ToHashSet();
        var operations = new List<SyncOperation>();
        foreach (var item in database.PendingSnapshot())
        {
            if (conflicts.Contains(item.RecordId) && item.Op != "delete") continue;
            var record = item.Record;
            if (item.Op == "delete" || record?.DeletedAt != null)
                operations.Add(new(item.OperationId, item.RecordId, item.BaseRevision, "delete", record?.Revision ?? item.BaseRevision + 1, null, null, true));
            else if (record is { Scope: SyncScope.Profile, Confirmed: true })
            {
                var (cipher, nonce) = MemoryCrypto.Encrypt(archiveKey, database.ProfileId, record.Id, record.Revision, epoch, MemoryCrypto.Payload(record));
                operations.Add(new(item.OperationId, record.Id, item.BaseRevision, "upsert", record.Revision, cipher, nonce, false));
            }
        }
        var accepted = 0;
        var rejected = 0;
        var epochRejected = false;
        foreach (var batch in operations.Chunk(SyncRules.MaxOperations))
        {
            using var push = await Send(http, HttpMethod.Post, serverUrl.TrimEnd('/') + "/v1/sync/push", deviceToken,
                new { profileId = database.ProfileId, epoch, operations = batch }, cancel);
            var results = await push.Content.ReadFromJsonAsync<List<SyncResult>>(Json, cancel)
                ?? throw new InvalidOperationException("同步回执为空，待上传改动仍保留。");
            if (results.Count != batch.Length || results.Select(r => r.OperationId).Distinct().Count() != batch.Length
                || results.Any(r => batch.All(op => op.OperationId != r.OperationId)))
                throw new InvalidOperationException("同步回执不完整，待上传改动仍保留。");
            foreach (var result in results)
            {
                var sent = batch.Single(op => op.OperationId == result.OperationId);
                if (result.Status is SyncStatus.Accepted or SyncStatus.Duplicate)
                {
                    if (result.ServerRevision != sent.Revision)
                        throw new InvalidOperationException("同步回执版本不匹配，待上传改动仍保留。");
                    database.Ack(result.OperationId, sent.Revision, sent.RecordId);
                    accepted++;
                }
                else if (result.Status == SyncStatus.EpochRejected) epochRejected = true;
                else if (result.Status == SyncStatus.Rejected) rejected++;
            }
            if (epochRejected) break;
        }
        var received = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            using var pull = await Send(http, HttpMethod.Get,
                $"{serverUrl.TrimEnd('/')}/v1/sync/pull?profileId={database.ProfileId}&after={cursor}&epoch={epoch}", deviceToken, null, cancel);
            var body = await pull.Content.ReadFromJsonAsync<PullBody>(Json, cancel)
                ?? throw new InvalidOperationException("同步拉取响应为空。");
            if (body.Reset || epochRejected || body.Epoch != epoch)
                return new("云端记忆已重置。本地改动还留着，请先导出恢复包，再接受云端重置。旧写入不会自动重传。", body.Epoch);
            if (body.Changes == null) throw new InvalidOperationException("同步拉取响应缺少变更列表。");
            var page = new List<MemoryRecord>();
            var pageCursor = cursor;
            foreach (var change in body.Changes)
            {
                if (change.ServerSequence <= pageCursor || change.Epoch != epoch)
                    throw new InvalidOperationException("同步变更顺序或代次无效，未跳过此页。");
                pageCursor = change.ServerSequence;
                if (change.Deleted)
                {
                    var existing = database.FindAny(change.RecordId);
                    page.Add((existing ?? new MemoryRecord(change.RecordId, database.ProfileId, MemoryKind.Note, "", [], MemorySource.User,
                        null, true, null, SyncScope.Profile, false, 0, 0, DateTimeOffset.UtcNow, null, false))
                        with { Revision = change.Revision, SyncedRevision = change.Revision, DeletedAt = DateTimeOffset.UtcNow, Content = "" });
                    continue;
                }
                var json = MemoryCrypto.Decrypt(archiveKey, database.ProfileId, change.RecordId, change.Revision, change.Epoch, change.Ciphertext, change.Nonce);
                var remote = JsonSerializer.Deserialize<RemoteMemory>(json, Json)
                    ?? throw new InvalidOperationException("同步记忆内容无效，未跳过此页。");
                if (remote.Content == null || remote.Scope != SyncScope.Profile || !remote.Confirmed)
                    throw new InvalidOperationException("同步记忆范围或确认状态无效，未跳过此页。");
                page.Add(new(change.RecordId, database.ProfileId, remote.Kind, remote.Content, remote.Tags ?? [], remote.Source,
                    remote.SourceId, remote.Confirmed, remote.ExpiresAt, remote.Scope, remote.AllowCloudModel, change.Revision, change.Revision,
                    remote.UpdatedAt == default ? DateTimeOffset.UtcNow : remote.UpdatedAt, null, remote.Pinned));
            }
            if (body.Cursor != pageCursor) throw new InvalidOperationException("同步游标与变更不一致，未跳过此页。");
            database.ApplyRemotePage(page, pageCursor, epoch);
            cursor = pageCursor;
            received += page.Count;
            if (page.Count < SyncRules.PullPageSize) break;
        }
        var pending = database.Outbox().Count;
        var unresolved = database.Conflicts().Count;
        var label = pending == 0 && unresolved == 0 && rejected == 0 ? "同步完成" : "同步尚未完成";
        return new($"{label}，已确认上传 {accepted} 条，收到 {received} 条；待上传 {pending} 条，冲突 {unresolved} 条，拒绝 {rejected} 条。", null);
    }

    public static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string? token, object? body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"同步服务返回 {status}，请检查连接或设备授权，待上传改动仍保留。");
        }
        return response;
    }

    public sealed record SyncReport(string Message, long? ResetEpoch);
    sealed record PullBody(long Cursor, long Epoch, bool Reset, List<SyncChange> Changes);
    sealed record RemoteMemory(MemoryKind Kind, string Content, string[]? Tags, MemorySource Source, string? SourceId, bool Confirmed,
        DateTimeOffset? ExpiresAt, SyncScope Scope, bool AllowCloudModel, bool Pinned, DateTimeOffset UpdatedAt);
}
