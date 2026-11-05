using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

internal static class CompanionCloud
{
    internal const string ScreenNotice = "OCR 在本机识别文字；云端模式会把识别出的屏幕文字发送至所选接口，不上传截图。关闭“感知当前应用”即可停止发送屏幕内容。";
    internal sealed record Settings(string BaseUrl, string ApiKey, string Model)
    {
        internal string ChatCompletionsUrl => BaseUrl.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? BaseUrl.TrimEnd('/') : BaseUrl.TrimEnd('/') + "/chat/completions";
    }
    internal readonly record struct Editor(string BaseUrl, string Model, string ApiKey, bool EnvironmentOverridesKey, string Sources, string Warning);

    internal static Editor LoadEditor() => Resolve(UserPath(), Environment.CurrentDirectory, AppContext.BaseDirectory, Environment.GetEnvironmentVariable);

    // One resolver for both the editor and requests. Null means absent; an empty key explicitly disables credentials.
    internal static Editor Resolve(string userPath, string workingDirectory, string programDirectory, Func<string, string?> environment)
    {
        string url = "https://api.deepseek.com", model = "deepseek-flash", key = "";
        string urlSource = "默认", modelSource = "默认", keySource = "未配置";
        var warnings = new List<string>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Apply(string? u, string? m, string? k, string source)
        {
            if (u != null) { url = u.Trim(); urlSource = source; }
            if (m != null) { model = m.Trim(); modelSource = source; }
            if (k != null) { key = k.Trim(); keySource = source; }
        }
        var files = new List<(string, string)>();
        foreach (var entry in new[] { (userPath, "用户设置"), (Path.Combine(workingDirectory, "cloud.json"), "工作目录"), (Path.Combine(programDirectory, "cloud.json"), "程序目录") })
            if (paths.Add(Path.GetFullPath(entry.Item1))) files.Add(entry);
        files.Reverse();
        foreach (var entry in files)
        {
            if (!File.Exists(entry.Item1)) continue;
            try
            {
                var file = JsonSerializer.Deserialize<FileSettings>(File.ReadAllText(entry.Item1), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (file == null) throw new JsonException();
                Apply(file.BaseUrl, file.Model, file.ApiKey, entry.Item2);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { warnings.Add(entry.Item2 + "的云端配置无法读取，已忽略。"); }
        }
        var environmentKey = environment("DEEPSEEK_API_KEY");
        Apply(environment("DEEPSEEK_BASE_URL"), environment("DEEPSEEK_MODEL"), environmentKey, "环境变量");
        return new Editor(url, model, key, environmentKey != null,
            $"地址：{urlSource}；模型：{modelSource}；Key：{keySource}", string.Join("\n", warnings));
    }

    static readonly HttpClient BalanceHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static bool IsDeepSeek(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("deepseek.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".deepseek.com", StringComparison.OrdinalIgnoreCase));

    internal static Uri BalanceUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) throw new IOException("接口地址无效。");
        return new UriBuilder(uri) { Path = "/user/balance", Query = "", Fragment = "" }.Uri;
    }

    internal readonly record struct BalanceReport(string Detail, string Speech);

    internal static Task<BalanceReport> QueryBalance(HttpClient? http = null) => QueryBalance(LoadEditor(), http);

    internal static async Task<BalanceReport> QueryBalance(Editor editor, HttpClient? http)
    {
        if (string.IsNullOrWhiteSpace(editor.ApiKey)) return new("尚未配置云端 API Key。", "主人，还没给鱼鱼 DeepSeek 的钥匙，鱼鱼查不到还剩多少呀。");
        Validate(editor.BaseUrl, editor.Model);
        if (!IsDeepSeek(editor.BaseUrl)) return new("目前只查询 DeepSeek 余额。当前保存的接口不是 DeepSeek。", "主人，鱼鱼现在只会看 DeepSeek 的小钱袋哦。");
        var client = http ?? BalanceHttp;
        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUri(editor.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", editor.ApiKey);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.SendAsync(request, cancel.Token);
            if ((int)response.StatusCode is 401 or 403) return new("余额查询鉴权失败，请检查 API Key。", "唔，钥匙好像不对，鱼鱼进不去看余额。");
            if (!response.IsSuccessStatusCode) return new($"余额查询失败（HTTP {(int)response.StatusCode}）。", "唔，鱼鱼这次没看成，主人等一下再让鱼鱼看吧。");
            return FormatBalance(await response.Content.ReadAsStringAsync(cancel.Token));
        }
        catch (OperationCanceledException) { return new("余额查询超时，请稍后重试。", "唔，等太久了，鱼鱼没看成余额。"); }
        catch (HttpRequestException) { return new("暂时连不上 DeepSeek 余额接口。", "唔，鱼鱼连不上，这次没看成余额。"); }
        catch (JsonException) { return new("余额响应无法读取。", "唔，鱼鱼看不懂这次的回信。"); }
    }

    internal static BalanceReport FormatBalance(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool available = !root.TryGetProperty("is_available", out var flag) || flag.ValueKind != JsonValueKind.False;
        if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
            return available
                ? new("已连上 DeepSeek，没有返回余额明细。", "主人，鱼鱼连上了，可是没看见还剩多少。")
                : new("DeepSeek 账户当前不可用。", "唔，主人，这个账户现在用不了了。");
        var details = new List<string>();
        var lines = new List<Money>();
        foreach (var info in infos.EnumerateArray())
        {
            string currency = Field(info, "currency");
            string total = Field(info, "total_balance");
            string granted = Field(info, "granted_balance");
            string name = currency.ToUpperInvariant() switch { "CNY" => "人民币", "USD" => "美元", "" or "—" => "余额", _ => currency };
            details.Add($"{name} 合计 {total}（充值 {Field(info, "topped_up_balance")}，赠送 {granted}）");
            lines.Add(new Money(name, MoneyText(currency, total), MoneyText(currency, granted), IsZero(total), IsPositive(granted)));
        }
        return new((available ? "DeepSeek 余额可用。" : "DeepSeek 账户当前不可用。") + string.Join("；", details), SpeakBalance(available, lines));
    }

    private readonly record struct Money(string Name, string Amount, string GiftText, bool Empty, bool Gift);

    static string SpeakBalance(bool available, List<Money> lines)
    {
        if (!available) return $"唔，主人，账户现在用不了，账上还记着 {lines[0].Amount}。";
        if (lines.Count == 1)
        {
            var line = lines[0];
            if (line.Empty) return "哎呀，主人，钱袋空空的了。";
            if (line.Gift) return $"主人，鱼鱼看过啦，还剩 {line.Amount}，里面 {line.GiftText}是送的嘞。";
            return $"主人，鱼鱼看过啦，还剩 {line.Amount}呢～";
        }
        var parts = new List<string>();
        foreach (var line in lines) parts.Add($"{line.Name}还剩 {line.Amount}");
        return "主人，鱼鱼看过啦，" + string.Join("，", parts) + "。";
    }

    static string MoneyText(string currency, string value) => currency.ToUpperInvariant() switch
    {
        "CNY" => value + " 元",
        "USD" => value + " 美元",
        _ => currency is "" or "—" ? value : value + " " + currency
    };

    static bool IsZero(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number == 0;
    static bool IsPositive(string value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0;

    static string Field(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "—";
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? "—" : value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            _ => "—"
        };
    }

    internal static Settings? TryLoad()
    {
        var editor = LoadEditor();
        return string.IsNullOrWhiteSpace(editor.ApiKey) ? null : new Settings(editor.BaseUrl, editor.ApiKey, editor.Model);
    }

    internal static Settings RequireSettings()
    {
        var settings = TryLoad() ?? throw new IOException("尚未配置云端 API Key");
        Validate(settings.BaseUrl, settings.Model);
        return settings;
    }

    internal static void Validate(string url, string model)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new IOException("接口地址需要是有效的 http:// 或 https:// 地址，不能包含账号、查询参数或片段。");
        if (string.IsNullOrWhiteSpace(model)) throw new IOException("请填写模型名称。");
    }

    internal static void SaveUser(string? baseUrl, string? model, string? apiKey) => Save(UserPath(), baseUrl, model, apiKey);
    internal static void Save(string path, string? baseUrl, string? model, string? apiKey)
    {
        string url = baseUrl?.Trim() ?? "", name = model?.Trim() ?? "";
        Validate(url, name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(JsonSerializer.Serialize(new FileSettings { BaseUrl = url, Model = name, ApiKey = apiKey?.Trim() ?? "" }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string UserPath()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BigBlueFish", "cloud.json");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config)) config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "bigbluefish", "cloud.json");
    }
    private sealed class FileSettings
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Model { get; set; }
    }
}
