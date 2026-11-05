using System.Net.Http.Json;
using System.Text.Json;
using BigBlueFish.Memory;
using BigBlueFish.Memory.Sync;

internal static class LiveSyncVerification
{
    public static async Task Run(string configurationPath, Action<bool, string> check)
    {
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath), SyncClient.Json)
            ?? throw new InvalidOperationException("Missing live test configuration");
        var root = Path.Combine(Path.GetTempPath(), "fish-live-sync-" + Guid.NewGuid().ToString("N"));
        var aPath = Path.Combine(root, "A");
        var bPath = Path.Combine(root, "B");
        using var a = CompanionMemory.Open(aPath);
        using var b = CompanionMemory.Open(bPath);
        Configure(a, config.ServerUrl); Configure(b, config.ServerUrl);
        var name = "isolated-live-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await a.Pair(config.Codes[0], name + "-A");
            a.CreateArchive();
            File.WriteAllText(configurationPath + ".cleanup.json", JsonSerializer.Serialize(new { profileId = a.Database.ProfileId }, SyncClient.Json));
            await b.Pair(config.Codes[1], name + "-B");
            var code = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await a.PublishEnvelope(code);
            await b.RedeemEnvelope(code);
            check(a.Database.ProfileId == b.Database.ProfileId, "LIVE: independent directories join the same encrypted archive");
            for (var i = 0; i < 225; i++) a.Remember("隔离验证记忆 " + i, false, false);
            a.Remember("仅本机测试内容", true, false);
            await Sync(a); await Sync(b);
            check(b.List().Count == 225 && a.Database.Outbox().Count == 0, "LIVE: 225 records traverse batched uploads and paginated pulls over HTTPS");
            var record = b.List().First();
            a.Update(record.Id, "离线先改", false, false);
            a.Update(record.Id, "离线再改", false, false);
            await Sync(a); await Sync(b);
            check(b.List().Single(row => row.Id == record.Id).Content == "离线再改", "LIVE: offline edits converge");
            a.Update(record.Id, "真实网络 A 版本", false, false);
            b.Update(record.Id, "真实网络 B 版本", false, false);
            await Sync(a);
            var status = await Sync(b);
            check(b.Conflicts().Count == 1 && status.Contains("尚未完成"), "LIVE: conflict is visible and retains both versions");
            b.ResolveConflict(record.Id, true);
            await Sync(b); await Sync(a);
            check(b.List().Single(row => row.Id == record.Id).Content == "真实网络 A 版本" && b.Database.Outbox().Count == 0,
                "LIVE: choosing cloud text resolves the conflict");
            b.Update(record.Id, "不应覆盖删除的旧文字", false, false);
            a.Delete(record.Id);
            await Sync(a); await Sync(b);
            check(b.List().All(row => row.Id != record.Id) && b.Database.Outbox().Count == 0, "LIVE: delete wins over a stale offline edit");
            check(!b.List().Any(row => row.Content == "仅本机测试内容"), "LIVE: local-only content is excluded");
            Console.WriteLine("LIVE-ISOLATED-SYNC-OK");
        }
        finally
        {
            await Revoke(aPath, config.ServerUrl);
            await Revoke(bPath, config.ServerUrl);
            Console.WriteLine("Live test device tokens revoked; synthetic archive ID saved for server-side cleanup.");
        }
    }

    static void Configure(CompanionMemory memory, string server)
    {
        memory.Enabled = true;
        memory.ServerUrl = server;
        memory.Passphrase = "live-verification-local-passphrase";
    }

    static async Task<string> Sync(CompanionMemory memory)
    {
        memory.SyncEnabled = true;
        try
        {
            var result = await memory.SyncNow();
            if (!result.StartsWith("同步完成") && !result.StartsWith("同步尚未完成"))
                throw new InvalidOperationException("Live synchronization failed: " + result);
            return result;
        }
        finally { memory.SyncEnabled = false; }
    }

    static async Task Revoke(string directory, string server)
    {
        var plain = SecretBox.TryLoad(Path.Combine(directory, "secrets.bin"), "live-verification-local-passphrase");
        if (plain == null) return;
        var identity = JsonSerializer.Deserialize<SyncSecrets>(plain, SyncClient.Json)!;
        if (string.IsNullOrEmpty(identity.DeviceToken)) return;
        using var http = new HttpClient(new HttpClientHandler { Proxy = new TailnetBypassProxy(HttpClient.DefaultProxy) })
            { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await SyncClient.Send(http, HttpMethod.Delete, server + "/v1/devices/" + identity.DeviceId, identity.DeviceToken, null, default);
    }

    sealed record Configuration(string ServerUrl, string[] Codes);
}
