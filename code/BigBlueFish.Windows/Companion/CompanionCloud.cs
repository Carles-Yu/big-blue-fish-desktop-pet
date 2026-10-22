using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
