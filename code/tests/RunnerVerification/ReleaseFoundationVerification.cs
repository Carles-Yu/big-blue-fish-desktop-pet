using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using BigBlueFish.Deployment;
using BigBlueFish.Windows;

internal static class ReleaseFoundationVerification
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static void Fails(Action action, string label) { try { action(); } catch (IOException) { Check(true, label); return; } throw new Exception(label); }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request)); }
    static string Release(string tag, string asset, bool prerelease = false, bool draft = false, bool digest = true) => JsonSerializer.Serialize(new {
        tag_name = tag, prerelease, draft, body = "notes", assets = new[] {
            new { name = asset, browser_download_url = "https://example.invalid/package", digest = digest ? "sha256:" + new string('a', 64) : "" },
            new { name = asset.EndsWith(".deb") ? asset + ".sha256" : asset.Replace(".exe", ".sha256"), browser_download_url = "https://example.invalid/checksum", digest = "" }
        }
    });
    static async Task ExpectFailure(HttpClient client, string message)
    {
        try { await UpdateService.Check("1.1.0", client: client); }
        catch (IOException e) { Check(e.Message.Contains(message), "update error: " + message); return; }
        throw new Exception("Expected update failure " + message);
    }
    internal static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Check(ReleaseIdentity.Repository == "https://github.com/Carles-Yu/big-blue-fish-desktop-pet", "fork identity");
        Check(ReleaseIdentity.Description.Contains("黎玊维护版") && ReleaseIdentity.Description.Contains("1.1.0"), "version and maintainer metadata");
        Check(UpdateService.LatestUrl.Contains("/Carles-Yu/"), "update source never upstream");
#if COMPANION_SAFE
        string prefix = "BigBlueFish-v1.2.0-Safe-";
#else
        string prefix = "BigBlueFish-v1.2.0-";
#endif
        foreach (bool linux in new[] { false, true }) {
            string asset = prefix + (linux ? "Linux-x64.deb" : "OCR-Setup.exe");
            foreach (bool digest in new[] { true, false }) {
                using var client = new HttpClient(new Handler(r => new(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri!.AbsolutePath == "/checksum" ? new string('a',64) : Release("v1.2.0", asset, digest: digest)) }));
                var found = await UpdateService.Check("1.1.0", client: client, linux: linux);
                Check(found?.Name == asset && found.Sha256 == new string('a',64), "platform asset/checksum " + linux + "/" + digest);
            }
        }
        foreach (var item in new[] { ("v1.1.0", false, false), ("v1.0.1", false, false), ("v1.2.0", true, false), ("v1.2.0", false, true) }) {
            using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release(item.Item1, prefix + "OCR-Setup.exe", item.Item2, item.Item3)) }));
            Check(await UpdateService.Check("1.1.0", client:client) == null, "ignore unchanged/old/prerelease/draft " + item);
        }
        foreach (var item in new[] { (404, "尚无"), (403, "限流"), (429, "限流"), (500, "连接失败") }) {
            using var client = new HttpClient(new Handler(_ => new((HttpStatusCode)item.Item1)));
            await ExpectFailure(client, item.Item2);
        }
        foreach (var body in new[] { "{", "{}", "[]" }) {
            using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) })); await ExpectFailure(client, "元数据");
        }
        using (var client = new HttpClient(new Handler(_ => throw new HttpRequestException("fixture")))) await ExpectFailure(client, "连接失败");
        using (var client = new HttpClient(new Handler(_ => throw new TaskCanceledException()))) await ExpectFailure(client, "超时");
        using (var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release("invalid", "wrong")) }))) await ExpectFailure(client, "标签");
        using (var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release("v1.2.0", "wrong")) }))) await ExpectFailure(client, "尚未就绪");
        var p = new CompanionPreferences { ModelMode = "Cloud" }; var before = CompanionSettingsSnapshot.From(p);
        p.GpuEnabled = !p.GpuEnabled; Check(!before.MustStop(CompanionSettingsSnapshot.From(p), true), "GPU change does not stop cloud");
        p.Scale = .7; p.WeatherRegionCode = "110101"; p.PublicInfo = false;
        Check(!before.MustStop(CompanionSettingsSnapshot.From(p), true), "layout and public information do not stop generation");
        Check(!before.MustStop(before, true), "identical settings have no cancellation");
        p.ModelMode = "Local"; Check(before.MustStop(CompanionSettingsSnapshot.From(p), true), "mode change invalidates request");
        p.ModelMode = "Cloud"; p.StopCloudOnBattery = true;
        Check(before.MustStop(CompanionSettingsSnapshot.From(p), false), "cloud battery transition cancels");
        Check(!before.MustStop(CompanionSettingsSnapshot.From(p), true), "AC policy changes do not cancel");
        p.ForegroundEnabled = false; Check(before.MustStop(CompanionSettingsSnapshot.From(p), true), "screen toggle cancels");
#if !COMPANION_SAFE
        foreach (bool toml in new[] { false, true }) {
            string path = Path.Combine(directory, Guid.NewGuid() + (toml ? ".toml" : ".json"));
            string bridge = "C:\\中文 空格\\O'Brien\\BigBlueFish.Mcp.exe";
            string original = toml ? "# keep\r\n[mcp_servers.\"big-blue-fish\"] # header\r\ncommand = 'old' # command\r\nargs = ['secret-argument']\r\n[mcp_servers.\"big-blue-fish\".env]\r\nTOKEN = 'private-fixture'\r\n[mcp_servers.other]\r\ncommand = 'other'\r\n" : "{\"otherSetting\":true,\"mcpServers\":{\"big-blue-fish\":{\"command\":\"old\",\"args\":[\"secret-argument\"],\"env\":{\"TOKEN\":\"private-fixture\"}},\"other\":{\"command\":\"other\"}}}";
            File.WriteAllText(path, original);
            var change = CompanionClientConfig.Prepare(path, toml, bridge);
            Check(change.Changed && !change.Preview.Contains("private-fixture") && !change.Preview.Contains("secret-argument"), "redacted change preview " + toml);
            CompanionClientConfig.Save(change);
            Check(File.ReadAllText(path).Contains("private-fixture") && File.ReadAllText(path).Contains("other"), "custom and other service fields retained " + toml);
            Check(Directory.GetFiles(directory, Path.GetFileName(path) + ".*.bak").Any(f => File.ReadAllText(f) == original), "exact backup " + toml);
            if (toml) Check(File.ReadAllText(path).Contains("# command") && File.ReadAllText(path).Contains("# header"), "TOML comments retained");
            Check(!CompanionClientConfig.Prepare(path, toml, bridge).Changed, "idempotent merge " + toml);
            var conflict = CompanionClientConfig.Prepare(path, toml, "new"); File.AppendAllText(path, " ");
            Fails(() => CompanionClientConfig.Save(conflict), "external edit rejected " + toml);
            var lockedChange = CompanionClientConfig.Prepare(path, toml, "locked");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Fails(() => CompanionClientConfig.Save(lockedChange), "locked config preserved " + toml);
            Check(!Directory.GetFiles(directory, "*.tmp").Any(), "no temporary file remains " + toml);
            string fresh = path + ".new"; CompanionClientConfig.Save(CompanionClientConfig.Prepare(fresh, toml, bridge));
            Check(!CompanionClientConfig.Prepare(fresh, toml, bridge).Changed, "new file roundtrip " + toml);
        }
        foreach (var item in new[] { (false,"{"), (false,"{\"mcpServers\":[]}"), (false,"{\"mcpServers\":{\"big-blue-fish\":null}}"), (true,"[broken"), (true,"mcp_servers = { big-blue-fish = { command = 'old' } }") }) {
            string path = Path.Combine(directory, Guid.NewGuid() + ".fixture"); File.WriteAllText(path,item.Item2);
            Fails(() => CompanionClientConfig.Prepare(path,item.Item1), "malformed/unsafe structure rejected"); Check(File.ReadAllText(path)==item.Item2,"rejected file unchanged");
        }
#endif
        Console.WriteLine($"PASS {checks} release foundation checks"); return 0;
    }
}
