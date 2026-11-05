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

internal sealed record CodexQuotaWindow(string Name, double? UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt)
{
    internal double? RemainingPercent => UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    internal string DurationLabel => WindowMinutes switch
    {
        300 => "5 小时", 10080 => "每周", 1440 => "每天", null => Name,
        int m when m > 0 && m % 1440 == 0 => $"{m / 1440} 天",
        int m when m > 0 && m % 60 == 0 => $"{m / 60} 小时",
        int m when m > 0 => $"{m} 分钟", _ => Name
    };
}
internal sealed record CodexQuotaBucket(string Name, CodexQuotaWindow[] Windows);
internal sealed record CodexQuotaReport(bool Success, string Message, CodexQuotaBucket[] Buckets, DateTimeOffset QueriedAt);

// Read-only account RPCs: never starts a thread/turn and never reads credential files itself.
internal static class CompanionCodexQuota
{
    internal static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("BIGBLUEFISH_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured) ? configured : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var appBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(appBin))
        {
            var bundled = Directory.EnumerateDirectories(appBin).Select(d => new FileInfo(Path.Combine(d, "codex.exe")))
                .Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (bundled != null) return bundled.FullName;
        }
        // npm installs expose a .cmd shim. Locate its native executable without executing a shell.
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        foreach (var root in new[] { Path.Combine(npm, "vendor"), Path.Combine(npm, "node_modules", "@openai", "codex-win32-x64", "vendor") })
        {
            var candidate = Path.Combine(root, "x86_64-pc-windows-msvc", "codex", "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static async Task<CodexQuotaReport> Query(CancellationToken token = default)
    {
        try
        {
            var executable = FindExecutable();
            if (executable == null) return Failure("未找到本机 Codex。请先安装 Codex 并登录；也可用 BIGBLUEFISH_CODEX_PATH 指定 codex.exe 的完整路径。");
            return Parse(await ReadAccountLimits(executable, token), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("查询超时，请检查 Codex 的网络连接后重试。"); }
        catch (OperationCanceledException) { throw; }
        catch (QuotaRpcException error) { return Failure(error.Message); }
        catch (JsonException) { return Failure("Codex 返回的额度格式无法读取，请更新 Codex 后重试。"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return Failure("无法启动或连接本机 Codex。请确认 Codex 可以正常运行，然后重试。"); }
    }
    private static CodexQuotaReport Failure(string message) => new(false, message, [], DateTimeOffset.UtcNow);

    internal static async Task<JsonElement> ReadAccountLimits(string executable, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        } };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen"); process.StartInfo.ArgumentList.Add("stdio://");
        using var job = new CompanionJob();
        if (!process.Start()) throw new IOException("Codex startup failed");
        Task stderr = Task.CompletedTask;
        try
        {
            if (!job.Attach(process)) throw new IOException("Codex process could not be contained");
            stderr = Drain(process.StandardError, ct);
            async Task Send(object message) => await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            async Task<JsonElement> Request(int id, string method, object? parameters = null)
            {
                await Send(new { id, method, @params = parameters ?? new { } });
                for (int i = 0; i < 256; i++)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line == null) throw new IOException("Codex disconnected");
                    if (line.Length > 1024 * 1024) throw new JsonException("Oversized response");
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var replyId)) continue;
                    if (root.TryGetProperty("method", out _))
                    {
                        // No credential-refresh/attestation/user actions are supported by this read-only client.
                        await Send(new { id = replyId.Clone(), error = new { code = -32601, message = "Read-only quota client" } });
                        continue;
                    }
                    if (replyId.ValueKind != JsonValueKind.Number || !replyId.TryGetInt32(out int number) || number != id) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        bool unsupported = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value == -32601;
                        throw new QuotaRpcException(unsupported ? "当前 Codex 不支持额度查询，请更新 Codex 后重试。" :
                            "未能读取套餐额度。请确认本机 Codex 已使用 ChatGPT 账户登录；API Key 登录不提供此套餐额度。");
                    }
                    if (!root.TryGetProperty("result", out var result)) throw new JsonException("Missing result");
                    return result.Clone();
                }
                throw new IOException("Too many notifications");
            }
            await Request(1, "initialize", new { clientInfo = new { name = "bigbluefish_quota", title = "BigBlueFish quota", version = "1.0.0" } });
            await Send(new { method = "initialized", @params = new { } });
            return await Request(2, "account/rateLimits/read");
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            deadline.Cancel();
            await stderr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    private static async Task Drain(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }

    internal static CodexQuotaReport Parse(JsonElement result, DateTimeOffset queriedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new JsonException("Missing limits");
        var buckets = new List<CodexQuotaBucket>();
        void Add(JsonElement bucket, string id)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return;
            var name = bucket.TryGetProperty("limitName", out var label) && label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString())
                ? label.GetString()! : id == "codex" ? "Codex" : id;
            var windows = new List<CodexQuotaWindow>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
                double? used = window.TryGetProperty("usedPercent", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var n) && double.IsFinite(n)
                    ? Math.Clamp(n, 0, 100) : null;
                int? minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var m) && m > 0 ? m : null;
                DateTimeOffset? reset = null;
                if (window.TryGetProperty("resetsAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out long seconds))
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
                windows.Add(new(key == "primary" ? "主要额度" : "次要额度", used, minutes, reset));
            }
            if (windows.Count > 0) buckets.Add(new(name, windows.ToArray()));
        }
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var bucket in map.EnumerateObject()) Add(bucket.Value, bucket.Name);
        if (buckets.Count == 0 && result.TryGetProperty("rateLimits", out var legacy)) Add(legacy, "codex");
        return new(true, buckets.Count == 0 ? "账户未返回额度窗口。当前额度未知，不代表已用完或无限量。" : "已读取本机 Codex 登录账户的额度。",
            buckets.ToArray(), queriedAt);
    }
    private sealed class QuotaRpcException(string message) : Exception(message);
}
