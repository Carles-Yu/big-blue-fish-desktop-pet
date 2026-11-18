using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

internal sealed record GeminiQuotaBucket(string Group, string Title, double? RemainingPercent, DateTimeOffset? ResetsAt, string? Note);
internal sealed record GeminiQuotaReport(bool Success, string Message, GeminiQuotaBucket[] Buckets, DateTimeOffset QueriedAt);

// Official read-only agy CLI only: never starts a model turn and never reads credential files itself.
internal static class CompanionGeminiQuota
{
    internal const string PathVariable = "BIGBLUEFISH_AGY_PATH";
    internal const string InvalidPathMessage = "环境变量 BIGBLUEFISH_AGY_PATH 不是有效的 agy.exe 绝对路径，请改成正确路径后再试。";
    internal const string NotFoundMessage = "未找到本机 agy。请先安装并登录 Antigravity 命令行；也可用 BIGBLUEFISH_AGY_PATH 指定 agy.exe 的完整路径。";
    private const int OutputLimit = 1024 * 1024;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    internal static bool IsValidAgyPath(string path) =>
        Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetFileName(path), "agy.exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    internal static string? ResolveExecutable(string? configured, IEnumerable<string> pathDirectories, string? defaultExecutable, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsValidAgyPath(configured)) return Path.GetFullPath(configured);
            error = InvalidPathMessage;
            return null;
        }
        foreach (var directory in pathDirectories)
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "agy.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        if (!string.IsNullOrWhiteSpace(defaultExecutable) && File.Exists(defaultExecutable)
            && string.Equals(Path.GetFileName(defaultExecutable), "agy.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(defaultExecutable);
        error = NotFoundMessage;
        return null;
    }

    internal static string? FindExecutable(out string? error) =>
        ResolveExecutable(
            Environment.GetEnvironmentVariable(PathVariable),
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin", "agy.exe"),
            out error);

    internal static Task<GeminiQuotaReport> Query(CancellationToken token = default) =>
        Query(null, token);

    internal static async Task<GeminiQuotaReport> Query(string? executable, CancellationToken token, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var queriedAt = DateTimeOffset.UtcNow;
        try
        {
            if (executable == null)
            {
                executable = FindExecutable(out var error);
                if (executable == null) return Failure(error ?? NotFoundMessage, queriedAt);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(QueryTimeout);
            var usageText = await Run(executable, "/usage", deadline.Token, extraEnvironment);
            var buckets = new List<GeminiQuotaBucket>();
            bool usageOk = TryParseUsage(usageText, buckets, out var usageError);

            GeminiQuotaBucket credits = UnknownCredits();
            try
            {
                var creditsText = await Run(executable, "/credits", deadline.Token, extraEnvironment);
                credits = ParseCredits(creditsText) ?? UnknownCredits();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                credits = UnknownCredits();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or JsonException)
            {
                credits = UnknownCredits();
            }

            buckets.Add(credits);
            if (!usageOk)
                return new(false, usageError ?? "未能读取额度。请先安装并登录 agy 后重试。", buckets.ToArray(), queriedAt);
            var message = buckets.Count == 1
                ? "账户未返回额度窗口。当前额度未知，不代表已用完或无限量。"
                : "已读取本机 agy 登录账户的额度。";
            return new(true, message, buckets.ToArray(), queriedAt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Failure("查询已取消。", queriedAt);
        }
        catch (OperationCanceledException)
        {
            return Failure("查询超时，请稍后重试。", queriedAt);
        }
        catch (JsonException)
        {
            return Failure("agy 返回的额度格式无法读取，请更新 agy 后重试。", queriedAt);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Failure("无法启动或连接本机 agy。请确认 agy 可以正常运行，然后重试。", queriedAt);
        }
    }

    internal static QuotaViewReport ToView(GeminiQuotaReport report) =>
        new(report.Success, report.Message,
            report.Buckets.Select(bucket => new QuotaCard(bucket.Group, bucket.Title, bucket.RemainingPercent, bucket.ResetsAt, bucket.Note)).ToArray(),
            report.QueriedAt);

    private static GeminiQuotaReport Failure(string message, DateTimeOffset queriedAt) => new(false, message, [], queriedAt);
    private static GeminiQuotaBucket UnknownCredits() => new("积分", "积分", null, null, null);

    private static async Task<string> Run(string executable, string prompt, CancellationToken token, IReadOnlyDictionary<string, string?>? extraEnvironment)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
        };
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(prompt);
        process.StartInfo.ArgumentList.Add("--output-format");
        process.StartInfo.ArgumentList.Add("json");
        if (extraEnvironment != null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                if (value == null) process.StartInfo.Environment.Remove(key);
                else process.StartInfo.Environment[key] = value;
            }
        }
        using var job = new CompanionJob();
        if (!process.Start()) throw new IOException("agy startup failed");
        Task stderr = Task.CompletedTask;
        try
        {
            if (!job.Attach(process)) throw new IOException("agy process could not be contained");
            stderr = Drain(process.StandardError, token);
            try { process.StandardInput.Close(); } catch (IOException) { }
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var text = await stdout;
            if (text.Length > OutputLimit) throw new JsonException("Oversized response");
            return text;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            await stderr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task Drain(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }

    internal static bool TryParseUsage(string text, List<GeminiQuotaBucket> buckets, out string? error)
    {
        error = null;
        if (!TryReadCommandData(text, "usage", out var data, out error)) return false;
        if (!data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return true;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var groupName = ReadString(group, "name");
            if (string.IsNullOrWhiteSpace(groupName)) groupName = "Gemini";
            if (!group.TryGetProperty("buckets", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in list.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                var title = WindowTitle(ReadString(bucket, "window"), ReadString(bucket, "name"), ReadString(bucket, "id"));
                buckets.Add(new(groupName, title, ReadRemainingPercent(bucket), ReadReset(bucket), null));
            }
        }
        return true;
    }

    internal static GeminiQuotaBucket? ParseCredits(string text)
    {
        if (!TryReadCommandData(text, "credits", out var data, out _)) return null;
        if (!data.TryGetProperty("remaining_credits", out var credits) || credits.ValueKind != JsonValueKind.Number)
            return UnknownCredits();
        string note;
        if (credits.TryGetInt64(out var integer))
            note = $"剩余 {integer} 积分";
        else if (credits.TryGetDouble(out var number) && double.IsFinite(number))
            note = number == Math.Truncate(number) ? $"剩余 {(long)number} 积分" : $"剩余 {number} 积分";
        else
            return UnknownCredits();
        return new("积分", "积分", null, null, note);
    }

    private static bool TryReadCommandData(string text, string expectedName, out JsonElement data, out string? error)
    {
        data = default;
        error = "未能读取额度。请先安装并登录 agy 后重试。";
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || !string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object)
            return false;
        if (command.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            var value = name.GetString();
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                error = "agy 返回的额度格式无法读取，请更新 agy 后重试。";
                return false;
            }
        }
        if (!command.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
        {
            error = "agy 返回的额度格式无法读取，请更新 agy 后重试。";
            return false;
        }
        data = data.Clone();
        error = null;
        return true;
    }

    private static string WindowTitle(string? window, string? name, string? id) => window switch
    {
        "weekly" => "周限额",
        "5h" => "5 小时限额",
        _ => !string.IsNullOrWhiteSpace(name) ? name! : !string.IsNullOrWhiteSpace(id) ? id! : "限额"
    };

    private static double? ReadRemainingPercent(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("remaining_fraction", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var fraction) || !double.IsFinite(fraction))
            return null;
        return Math.Clamp(fraction * 100, 0, 100);
    }

    private static DateTimeOffset? ReadReset(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("reset_time", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(value.GetString(), out var reset) ? reset : null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
