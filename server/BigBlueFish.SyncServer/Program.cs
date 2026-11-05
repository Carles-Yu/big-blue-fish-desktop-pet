using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BigBlueFish.Memory.Sync;
using BigBlueFish.SyncServer;

var databaseUrl = Environment.GetEnvironmentVariable("SYNC_DATABASE_URL");
var adminToken = Environment.GetEnvironmentVariable("SYNC_ADMIN_TOKEN");
if (string.IsNullOrWhiteSpace(databaseUrl))
{
    Console.Error.WriteLine("缺少 SYNC_DATABASE_URL。数据库只监听虚拟机内部，不要写进镜像。");
    return 1;
}

var dataSource = Npgsql.NpgsqlDataSource.Create(databaseUrl);
var store = new PostgresStore(dataSource);
store.Migrate(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "001_init.sql")));
var accountId = store.Bootstrap();

if (args.Contains("--pair"))
{
    if (string.IsNullOrWhiteSpace(adminToken)) { Console.Error.WriteLine("缺少 SYNC_ADMIN_TOKEN。"); return 1; }
    Console.WriteLine(store.CreatePairing(accountId));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var app = builder.Build();
var engine = new SyncEngine(store);
var pairingHits = new ConcurrentDictionary<string, (int Count, DateTime Window)>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/admin/pairings", (HttpContext context) =>
{
    if (!Admin(context)) return Results.Unauthorized();
    var code = store.CreatePairing(accountId);
    app.Logger.LogInformation("pairing created");
    return Results.Ok(new { code, expiresInSeconds = 900 });
});

app.MapPost("/v1/device-pairings/complete", async (HttpContext context) =>
{
    if (!Allow(pairingHits, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 8, TimeSpan.FromMinutes(10)))
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    var body = await context.Request.ReadFromJsonAsync<PairingBody>();
    if (body == null || string.IsNullOrWhiteSpace(body.Code)) return Results.BadRequest(new { error = "缺少配对码" });
    var paired = store.CompletePairing(body.Code, string.IsNullOrWhiteSpace(body.DeviceName) ? "设备" : body.DeviceName);
    if (paired == null) return Results.BadRequest(new { error = "配对码无效或已使用" });
    app.Logger.LogInformation("device paired {DeviceId}", paired.Value.DeviceId);
    return Results.Ok(new { accountId = paired.Value.AccountId, deviceId = paired.Value.DeviceId, deviceToken = paired.Value.Token });
});

app.MapGet("/v1/devices", (HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    return Results.Ok(store.Devices(device.Value.AccountId));
});

app.MapDelete("/v1/devices/{id:guid}", (Guid id, HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    return store.Revoke(device.Value.AccountId, id) ? Results.Ok(new { revoked = true }) : Results.NotFound();
});

app.MapPost("/v1/key-envelopes", async (HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    var body = await context.Request.ReadFromJsonAsync<EnvelopeBody>();
    if (body == null || string.IsNullOrWhiteSpace(body.TransferCode) || string.IsNullOrWhiteSpace(body.Wrapped))
        return Results.BadRequest(new { error = "缺少传递口令或密钥信封" });
    store.PublishEnvelope(device.Value.AccountId, body.TransferCode, body.Wrapped);
    return Results.Ok(new { stored = true });
});

app.MapPost("/v1/key-envelopes/redeem", async (HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    var body = await context.Request.ReadFromJsonAsync<EnvelopeBody>();
    if (body == null || string.IsNullOrWhiteSpace(body.TransferCode)) return Results.BadRequest(new { error = "缺少传递口令" });
    var wrapped = store.RedeemEnvelope(device.Value.AccountId, body.TransferCode);
    return wrapped == null ? Results.BadRequest(new { error = "传递口令无效或已使用" }) : Results.Ok(new { wrapped });
});

app.MapPost("/v1/sync/push", async (HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    var body = await context.Request.ReadFromJsonAsync<PushBody>(SyncClient.Json);
    if (body == null || body.Operations == null) return Results.BadRequest(new { error = "缺少同步操作" });
    if (body.Operations.Count > SyncRules.MaxOperations) return Results.BadRequest(new { error = "单次同步操作过多" });
    var started = DateTime.UtcNow;
    var results = engine.Push(device.Value.AccountId, device.Value.DeviceId, body.ProfileId, body.Epoch, body.Operations);
    foreach (var result in results)
        app.Logger.LogInformation("sync {OperationId} {Status} {ElapsedMs}", result.OperationId, result.Status, (DateTime.UtcNow - started).TotalMilliseconds);
    return Results.Ok(results);
});

app.MapGet("/v1/sync/pull", (Guid profileId, long after, long epoch, HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    store.EnsureProfile(device.Value.AccountId, profileId);
    var page = store.ReadPage(device.Value.AccountId, profileId, after);
    var serverEpoch = page.Epoch;
    if (epoch != serverEpoch) return Results.Ok(new { cursor = after, epoch = serverEpoch, reset = true, changes = Array.Empty<SyncChange>() });
    var changes = page.Changes;
    return Results.Ok(new { cursor = changes.LastOrDefault()?.ServerSequence ?? after, epoch = serverEpoch, reset = false, changes });
});

app.MapGet("/v1/sync/snapshot", (Guid profileId, HttpContext context) =>
{
    var device = Device(context);
    if (device == null) return Results.Unauthorized();
    store.EnsureProfile(device.Value.AccountId, profileId);
    var epoch = store.Epoch(device.Value.AccountId, profileId);
    var changes = store.Changes(device.Value.AccountId, profileId, 0, SyncRules.MaxRecords);
    var latest = changes.GroupBy(change => change.RecordId).Select(group => group.Last()).ToArray();
    return Results.Ok(new { epoch, changes = latest });
});

app.MapPost("/v1/profiles/{id:guid}/reset", (Guid id, HttpContext context) =>
{
    if (!Admin(context)) return Results.Unauthorized();
    store.EnsureProfile(accountId, id);
    store.Reset(accountId, id);
    app.Logger.LogInformation("profile reset {ProfileId}", id);
    return Results.Ok(new { epoch = store.Epoch(accountId, id) });
});

app.Run();
return 0;

bool Admin(HttpContext context)
{
    if (string.IsNullOrWhiteSpace(adminToken)) return false;
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
    var given = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
    var expected = Encoding.UTF8.GetBytes(adminToken);
    return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
}

(Guid AccountId, Guid DeviceId)? Device(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
    return store.Authenticate(header["Bearer ".Length..].Trim());
}

static bool Allow(ConcurrentDictionary<string, (int Count, DateTime Window)> hits, string key, int limit, TimeSpan window)
{
    var now = DateTime.UtcNow;
    var current = hits.AddOrUpdate(key, (1, now), (_, old) => now - old.Window > window ? (1, now) : (old.Count + 1, old.Window));
    return current.Count <= limit;
}

sealed record PairingBody(string? Code, string? DeviceName);
sealed record EnvelopeBody(string? TransferCode, string? Wrapped);
sealed record PushBody(Guid ProfileId, long Epoch, List<SyncOperation>? Operations);
