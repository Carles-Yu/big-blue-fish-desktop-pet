using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BigBlueFish.Memory;
using BigBlueFish.Memory.Sync;

internal static class SyncVerification
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fish-sync-pair-" + Guid.NewGuid().ToString("N"));
        var server = new TestServer();
        using var a = CompanionMemory.Open(Path.Combine(root, "A"), new HttpClient(server, false));
        using var b = CompanionMemory.Open(Path.Combine(root, "B"), new HttpClient(server, false));
        Configure(a); Configure(b);
        a.Remember("本机已有的偏好", false, false);
        await a.Pair("test-A", "A");
        a.SyncEnabled = true;
        check((await a.SyncNow()).Contains("尚未接入"), "Pairing cannot silently create a separate archive");
        a.SyncEnabled = false;
        a.CreateArchive();
        await b.Pair("test-B", "B");
        var oldB = b.Database.ProfileId;
        const string code = "a-long-random-transfer-test-code";
        await a.PublishEnvelope(code);
        await b.RedeemEnvelope(code);
        check(a.Database.ProfileId == b.Database.ProfileId && oldB != b.Database.ProfileId, "Transfer joins the same archive ID, not just the same key");
        using (var reopened = MemoryDatabase.Open(Path.Combine(root, "B")))
            check(reopened.ProfileId == a.Database.ProfileId, "Joined archive ID persists across reopening");
        for (var i = 0; i < 425; i++) a.Remember("批量偏好 " + i, false, false);
        a.Remember("只留在本机的事项", true, false);
        a.Database.Add(new(MemoryKind.Note, "未确认候选", [], MemorySource.Candidate, null, false, null, SyncScope.Profile, false, false));
        await Sync(a); await Sync(b);
        check(b.List().Count == 426 && a.Database.Outbox().Count == 0, "More than 400 memories upload in batches and download across pages");
        check(server.BatchSizes.Count > 8 && server.BatchSizes.Max() == 50, "Every upload respects the server's 50-operation limit");
        check(!b.List().Any(item => item.Content.Contains("只留") || item.Content == "未确认候选"), "Local-only and unconfirmed content never leaves the device");
        var madePrivate = a.List().First(row => row.Content == "批量偏好 11");
        a.Update(madePrivate.Id, "改成只在本机保留的文字", true, false);
        await Sync(a); await Sync(b);
        check(b.List().All(row => row.Id != madePrivate.Id) && a.List().Any(row => row.Content == "改成只在本机保留的文字" && row.Scope == SyncScope.LocalOnly),
            "Making a shared memory private deletes its cloud copy without erasing the local copy");
        var item = b.List().First(row => row.Content == "批量偏好 10");
        a.Update(item.Id, "离线修改一次", false, false);
        a.Update(item.Id, "离线修改两次", false, false);
        await Sync(a); await Sync(b);
        check(b.List().Single(row => row.Id == item.Id).Content == "离线修改两次", "Repeated offline edits retain the acknowledged base revision");
        a.Update(item.Id, "A 的并发文字", false, false);
        b.Update(item.Id, "B 的并发文字", false, false);
        await Sync(a);
        var conflictStatus = await Sync(b);
        check(b.Conflicts().Count == 1 && conflictStatus.Contains("尚未完成"), "Conflicts retain both versions and are not reported as complete");
        b.ResolveConflict(item.Id, false);
        await Sync(b); await Sync(a);
        check(a.List().Single(row => row.Id == item.Id).Content == "B 的并发文字" && b.Conflicts().Count == 0,
            "Explicit conflict resolution converges without losing the selected text");
        b.Update(item.Id, "B 尚未同步的文字", false, false);
        a.Delete(item.Id);
        await Sync(a); await Sync(b);
        check(b.List().All(row => row.Id != item.Id) && b.Database.Outbox().Count == 0, "Remote deletion wins over an offline edit");
        var retry = a.Remember("回执丢失后重试", false, false);
        server.LoseNextPushReply = true;
        var failed = await Sync(a);
        check(a.Database.Outbox().Count == 1 && !failed.StartsWith("同步完成"), "Lost acknowledgement retains the operation for retry");
        await Sync(a); await Sync(b);
        check(b.List().Count(row => row.Id == retry.Id) == 1 && a.Database.Outbox().Count == 0,
            "Retry after server commit does not duplicate a memory");
        a.Update(retry.Id, "网络请求中的旧版本", false, false);
        server.BeforeNextPushReply = () => a.Update(retry.Id, "请求发出后的新版本", false, false);
        await Sync(a);
        check(a.Database.Outbox().Count == 1, "An edit made during upload is not acknowledged as the older operation");
        await Sync(a); await Sync(b);
        check(b.List().Single(row => row.Id == retry.Id).Content == "请求发出后的新版本", "Edits made during upload converge on a later retry");
        using var c = CompanionMemory.Open(Path.Combine(root, "C"), new HttpClient(server, false));
        Configure(c);
        c.ImportRecovery(a.ExportRecovery());
        await c.Pair("test-C", "C");
        check(c.Database.ProfileId == a.Database.ProfileId, "Recovery import followed by pairing retains the original archive");
        server.CorruptNextPull = true;
        var corrupt = await Sync(c);
        check(c.List().Count == 0 && c.Database.SyncCursor().Cursor == 0 && !corrupt.StartsWith("同步完成"),
            "An invalid encrypted page neither partly applies records nor advances its cursor");
        await Sync(c);
        check(c.List().Count == b.List().Count, "Retry after a bad page recovers all valid records");
        a.ServerUrl = "https://another.example";
        var calls = server.Requests;
        check((await Sync(a)).Contains("另一台") && server.Requests == calls, "Changing servers does not forward an existing device token");
        a.ServerUrl = "https://test.example";
        a.Remember("清空前的离线改动", false, false);
        var local = b.Remember("重置后仍保留本机文字", true, false);
        server.Store.Reset(server.Account, a.Database.ProfileId);
        await Sync(a); await Sync(b);
        check(a.PendingCloudEpoch == 2 && a.Database.Outbox().Count > 0, "Cloud reset rejects old operations without silently discarding them");
        a.AcceptCloudReset(); b.AcceptCloudReset();
        await Sync(a); await Sync(b);
        check(a.Database.Outbox().Count == 0 && a.List().All(row => row.Scope == SyncScope.LocalOnly)
            && b.List().Any(row => row.Id == local.Id), "Accepting cloud reset clears shared records while preserving local-only memory");
        Console.WriteLine("ISOLATED-SYNC-OK");
    }

    static void Configure(CompanionMemory memory)
    {
        memory.Enabled = true;
        memory.SyncEnabled = false;
        memory.ServerUrl = "https://test.example";
        memory.Passphrase = "verification-local-passphrase";
    }

    static async Task<string> Sync(CompanionMemory memory)
    {
        memory.SyncEnabled = true;
        try { return await memory.SyncNow(); }
        finally { memory.SyncEnabled = false; }
    }

    sealed class TestServer : HttpMessageHandler
    {
        public readonly Guid Account = Guid.NewGuid();
        public readonly MemorySyncStore Store = new();
        public readonly List<int> BatchSizes = [];
        public bool LoseNextPushReply;
        public bool CorruptNextPull;
        public Action? BeforeNextPushReply;
        public int Requests;
        readonly Dictionary<string, Guid> tokens = new();
        readonly Dictionary<string, string> envelopes = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/device-pairings/complete")
            {
                var id = Guid.NewGuid();
                var token = Guid.NewGuid().ToString("N");
                tokens[token] = id;
                return Reply(new { accountId = Account, deviceId = id, deviceToken = token });
            }
            if (!tokens.TryGetValue(request.Headers.Authorization?.Parameter ?? "", out var device))
                return new(HttpStatusCode.Unauthorized);
            if (path.StartsWith("/v1/key-envelopes"))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancel);
                var code = body.GetProperty("transferCode").GetString()!;
                if (path.EndsWith("/redeem"))
                {
                    if (!envelopes.Remove(code, out var wrapped)) return new(HttpStatusCode.BadRequest);
                    return Reply(new { wrapped });
                }
                envelopes[code] = body.GetProperty("wrapped").GetString()!;
                return Reply(new { stored = true });
            }
            if (path == "/v1/sync/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<Push>(SyncClient.Json, cancel);
                BatchSizes.Add(body!.Operations.Length);
                if (body.Operations.Length > SyncRules.MaxOperations) return new(HttpStatusCode.BadRequest);
                var results = new SyncEngine(Store).Push(Account, device, body.ProfileId, body.Epoch, body.Operations);
                var hook = BeforeNextPushReply; BeforeNextPushReply = null; hook?.Invoke();
                if (LoseNextPushReply) { LoseNextPushReply = false; throw new HttpRequestException("simulated lost acknowledgement"); }
                return Reply(results);
            }
            if (path == "/v1/sync/pull")
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=')).ToDictionary(p => p[0], p => p[1]);
                var profile = Guid.Parse(query["profileId"]);
                var after = long.Parse(query["after"]);
                Store.EnsureProfile(Account, profile);
                var epoch = Store.Epoch(Account, profile);
                if (epoch != long.Parse(query["epoch"])) return Reply(new { cursor = after, epoch, reset = true, changes = Array.Empty<SyncChange>() });
                var changes = Store.Changes(Account, profile, after, SyncRules.PullPageSize).ToArray();
                if (CorruptNextPull && changes.Length > 1)
                {
                    CorruptNextPull = false;
                    changes[1] = changes[1] with { Ciphertext = "invalid-encrypted-page" };
                }
                return Reply(new { cursor = changes.LastOrDefault()?.ServerSequence ?? after, epoch, reset = false, changes });
            }
            return new(HttpStatusCode.NotFound);
        }

        static HttpResponseMessage Reply<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: SyncClient.Json) };
        sealed record Push(Guid ProfileId, long Epoch, SyncOperation[] Operations);
    }
}
