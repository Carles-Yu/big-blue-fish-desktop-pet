using System.Net.Http.Json;
using System.Text.Json;

namespace BigBlueFish.Memory;

public sealed record SyncSecrets(Guid AccountId, Guid DeviceId, string DeviceToken, string ArchiveKey,
    bool ArchiveReady = true, string? ServerIdentity = null);

public sealed class CompanionMemory : IDisposable
{
    readonly string directory;
    readonly HttpClient http;
    readonly SemaphoreSlim syncGate = new(1, 1);
    MemoryDatabase? database;
    SyncSecrets? secrets;
    public bool Enabled { get; set; }
    public bool SyncEnabled { get; set; }
    public string? ServerUrl { get; set; }
    public string? Passphrase { get; set; }
    public string Status { get; private set; } = "记忆默认关闭。";
    public long? PendingCloudEpoch { get; private set; }

    CompanionMemory(string directory, HttpClient? client)
    {
        this.directory = directory;
        http = client ?? new HttpClient(new HttpClientHandler { Proxy = new Sync.TailnetBypassProxy(HttpClient.DefaultProxy) })
            { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static CompanionMemory Open(string? directory = null, HttpClient? client = null) => new(directory ?? MemoryPaths.DirectoryForUser(), client);

    public MemoryDatabase Database => database ??= MemoryDatabase.Open(directory);

    public string Prompt(string scene, bool cloud, string kind)
    {
        if (!Enabled) return "";
        try { return Database.Prompt(scene, cloud, kind); }
        catch (Exception ex) { Status = ex.Message; return ""; }
    }

    public IReadOnlyList<MemoryRecord> List() => Enabled || database != null ? Database.List() : [];
    public IReadOnlyList<MemoryConflict> Conflicts() => database == null ? [] : Database.Conflicts();
    public IReadOnlyList<LocalMessage> Recent() => database == null ? [] : Database.RecentMessages(12);

    public MemoryRecord Remember(string content, bool localOnly, bool allowCloud)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("内容是空的。");
        ArmIfReady();
        var kind = content.Contains('叫') || content.Contains("名字") || content.Contains("喜欢") || content.Contains("习惯") ? MemoryKind.Profile : MemoryKind.Note;
        var saved = Database.Add(new MemoryDraft(kind, content, [], MemorySource.User, null, true, null,
            localOnly ? SyncScope.LocalOnly : SyncScope.Profile, allowCloud && !localOnly, false));
        ScheduleSync();
        return saved;
    }

    public void Update(Guid id, string content, bool localOnly, bool allowCloud)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("内容是空的。");
        var current = Database.List().First(row => row.Id == id);
        ArmIfReady();
        Database.Update(id, new MemoryDraft(current.Kind, content, current.Tags, MemorySource.User, current.SourceId, true,
            current.ExpiresAt, localOnly ? SyncScope.LocalOnly : SyncScope.Profile, allowCloud && !localOnly, current.Pinned));
        ScheduleSync();
    }

    public void Delete(Guid id)
    {
        ArmIfReady();
        Database.Delete(id);
        ScheduleSync();
    }

    public void AddChat(string role, string content) { if (Enabled) Database.AddMessage(role, content); }

    public async Task<string> Pair(string code, string deviceName)
    {
        RequireServer();
        LoadSecrets();
        CheckServerIdentity();
        CheckSecretStorage();
        using var response = await Sync.SyncClient.Send(http, HttpMethod.Post, ServerUrl!.TrimEnd('/') + "/v1/device-pairings/complete", null,
            new { code, deviceName }, CancellationToken.None);
        var paired = await response.Content.ReadFromJsonAsync<Paired>(Sync.SyncClient.Json) ?? throw new InvalidOperationException("配对响应为空");
        var key = secrets?.ArchiveKey ?? Convert.ToBase64String(MemoryCrypto.NewKey());
        secrets = new SyncSecrets(paired.AccountId, paired.DeviceId, paired.DeviceToken, key, secrets?.ArchiveReady ?? false, NormalizeServer(ServerUrl!));
        SaveSecrets();
        Status = "设备已配对。第一台设备请创建记忆档案；接入已有档案请导入传递口令或恢复包。";
        return Status;
    }

    public void CreateArchive()
    {
        RequireSecrets();
        if (secrets!.ArchiveReady) throw new InvalidOperationException("这台设备已有记忆档案，不会覆盖原密钥。");
        secrets = secrets with { ArchiveReady = true };
        SaveSecrets();
        Database.ArmSync();
        Status = "已创建记忆档案，可以同步。其他设备请通过传递口令加入这份档案。";
    }

    public async Task PublishEnvelope(string transferCode)
    {
        RequireArchive();
        CheckTransferCode(transferCode);
        var archive = JsonSerializer.SerializeToUtf8Bytes(new ArchiveTransfer(Database.ProfileId, secrets!.ArchiveKey), Sync.SyncClient.Json);
        var wrapped = MemoryCrypto.WrapKey(transferCode, archive);
        using var response = await Sync.SyncClient.Send(http, HttpMethod.Post, ServerUrl!.TrimEnd('/') + "/v1/key-envelopes", secrets.DeviceToken,
            new { transferCode, wrapped }, CancellationToken.None);
        Status = "传递口令已生效。请在另一台设备输入同一个口令，口令本身不会留在服务器。";
    }

    public async Task RedeemEnvelope(string transferCode)
    {
        RequireSecrets();
        CheckTransferCode(transferCode);
        CheckSecretStorage();
        using var response = await Sync.SyncClient.Send(http, HttpMethod.Post, ServerUrl!.TrimEnd('/') + "/v1/key-envelopes/redeem", secrets!.DeviceToken,
            new { transferCode }, CancellationToken.None);
        var body = await response.Content.ReadFromJsonAsync<Envelope>(Sync.SyncClient.Json) ?? throw new InvalidOperationException("没有密钥信封");
        var plain = MemoryCrypto.UnwrapKey(transferCode, body.Wrapped);
        var archive = JsonSerializer.Deserialize<ArchiveTransfer>(plain, Sync.SyncClient.Json)
            ?? throw new InvalidOperationException("传递包缺少档案编号，请用新版重新发布。");
        ValidateArchive(archive.ProfileId, archive.ArchiveKey);
        Database.AdoptProfile(archive.ProfileId);
        secrets = secrets with { ArchiveKey = archive.ArchiveKey, ArchiveReady = true };
        SaveSecrets();
        Database.ArmSync();
        Status = "已加入同一份记忆档案，档案编号与密钥均已导入。";
    }

    public async Task<string> SyncNow()
    {
        if (!Enabled || !SyncEnabled) return "记忆或同步还没开启。";
        LoadSecrets();
        if (secrets == null) return OperatingSystem.IsWindows()
            ? "还没有配对。请向管理员要一次性配对码。"
            : "Linux 需要先填写同步口令，再配对。密钥不会明文保存。";
        if (!await syncGate.WaitAsync(0)) return Status;
        try
        {
            RequireArchive();
            Database.ArmSync();
            var report = await new Sync.SyncClient().Synchronize(Database, Convert.FromBase64String(secrets.ArchiveKey), secrets.DeviceId, secrets.DeviceToken, ServerUrl!.TrimEnd('/'), http, CancellationToken.None);
            PendingCloudEpoch = report.ResetEpoch;
            Status = report.Message;
            return Status;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return Status;
        }
        finally { syncGate.Release(); }
    }

    public void AcceptCloudReset()
    {
        if (PendingCloudEpoch is not long epoch) throw new InvalidOperationException("云端没有待接受的重置。");
        Database.AcceptReset(epoch);
        PendingCloudEpoch = null;
        Status = "已改跟云端的新版本。之前没同步上去的改动不再自动上传。";
    }

    public string ExportRecovery()
    {
        RequireArchive();
        return JsonSerializer.Serialize(new { profileId = Database.ProfileId, serverUrl = ServerUrl, archiveKey = secrets!.ArchiveKey, warning = "这份文件可以解开云端记忆，用完请从聊天记录里删除。" }, Sync.SyncClient.Json);
    }

    public void ImportRecovery(string json)
    {
        var body = JsonSerializer.Deserialize<Recovery>(json, Sync.SyncClient.Json) ?? throw new InvalidOperationException("恢复包无法读取");
        ValidateArchive(body.ProfileId, body.ArchiveKey);
        NormalizeServer(body.ServerUrl);
        CheckSecretStorage();
        LoadSecrets();
        if (secrets?.ServerIdentity is { } bound && bound != NormalizeServer(body.ServerUrl))
            throw new InvalidOperationException("恢复包属于另一台同步服务器，请使用独立档案目录导入。");
        Database.AdoptProfile(body.ProfileId);
        secrets = new SyncSecrets(secrets?.AccountId ?? Guid.Empty, secrets?.DeviceId ?? Guid.Empty, secrets?.DeviceToken ?? "", body.ArchiveKey, true, NormalizeServer(body.ServerUrl));
        ServerUrl = body.ServerUrl;
        SaveSecrets();
        Database.ArmSync();
        Status = "已导入档案密钥。设备仍需单独配对后才能同步。";
    }

    public void Dispose() { scheduled?.Cancel(); database?.Dispose(); http.Dispose(); }

    void RequireServer()
    {
        NormalizeServer(ServerUrl ?? "");
    }

    static string NormalizeServer(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("同步地址需要 HTTPS；HTTP 仅限本机测试，不可包含账号、查询参数或片段。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    void CheckServerIdentity()
    {
        if (secrets?.ServerIdentity is { } bound && bound != NormalizeServer(ServerUrl!))
            throw new InvalidOperationException("当前密钥绑定了另一台服务器，请使用独立档案目录迁移，未向新服务器发送凭据。");
    }

    void CheckSecretStorage()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Passphrase))
            throw new InvalidOperationException("请先填写 Linux 本机同步口令，再配对或导入密钥。");
    }

    static void CheckTransferCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 16)
            throw new InvalidOperationException("传递口令至少 16 个字符，请使用一段足够长且不重复使用的口令。");
    }

    static void ValidateArchive(Guid profile, string key)
    {
        if (profile == Guid.Empty || Convert.FromBase64String(key).Length != 32)
            throw new InvalidOperationException("档案编号或密钥无效。");
    }

    void RequireSecrets()
    {
        LoadSecrets();
        if (secrets == null || string.IsNullOrEmpty(secrets.DeviceToken)) throw new InvalidOperationException("这台设备还没有配对。");
        RequireServer();
        CheckServerIdentity();
    }

    void RequireArchive()
    {
        RequireSecrets();
        if (!secrets!.ArchiveReady) throw new InvalidOperationException("尚未接入记忆档案：第一台请创建档案，其他设备请导入传递口令。");
    }

    void ArmIfReady()
    {
        LoadSecrets();
        if (secrets?.ArchiveReady == true && !Database.SyncArmed) Database.ArmSync();
    }

    public void ResolveConflict(Guid id, bool useCloudText)
    {
        Database.ResolveConflict(id, useCloudText);
        ScheduleSync();
    }

    void LoadSecrets()
    {
        if (secrets != null) return;
        try
        {
            var plain = SecretBox.TryLoad(Path.Combine(directory, "secrets.bin"), Passphrase);
            if (plain == null) return;
            secrets = JsonSerializer.Deserialize<SyncSecrets>(plain, Sync.SyncClient.Json);
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    CancellationTokenSource? scheduled;
    void ScheduleSync()
    {
        if (!SyncEnabled || secrets == null) return;
        scheduled?.Cancel();
        var tokenSource = scheduled = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), tokenSource.Token); await SyncNow(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = ex.Message; }
        });
    }

    void SaveSecrets()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets, Sync.SyncClient.Json);
        SecretBox.Save(Path.Combine(directory, "secrets.bin"), plain, Passphrase);
    }

    sealed record Paired(Guid AccountId, Guid DeviceId, string DeviceToken);
    sealed record Envelope(string Wrapped);
    sealed record Recovery(Guid ProfileId, string ServerUrl, string ArchiveKey);
    sealed record ArchiveTransfer(Guid ProfileId, string ArchiveKey);
}
