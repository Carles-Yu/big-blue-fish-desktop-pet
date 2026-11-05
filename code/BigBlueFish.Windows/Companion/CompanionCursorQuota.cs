using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BigBlueFish.Windows;

internal sealed record CursorQuotaLine(string Title, double? RemainingPercent, DateTimeOffset? ResetsAt, string? Note);

internal sealed record CursorQuotaReport(bool Success, string Message, CursorQuotaLine[] Lines, DateTimeOffset QueriedAt);

/// <summary>
/// Reads the local Cursor login only while a quota window is open, then asks cursor.com for usage.
/// The session value is never written to logs, chat, settings, or the window text.
/// </summary>
internal static class CompanionCursorQuota
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    internal static Task<CursorQuotaReport> Query(CancellationToken cancellation = default) =>
        Query(null, null, DatabasePath, cancellation);

    internal static async Task<CursorQuotaReport> Query(
        HttpMessageHandler? handler, string? accessToken, string? databasePath, CancellationToken cancellation = default)
    {
        var queriedAt = DateTimeOffset.Now;
        string token;
        if (accessToken != null)
            token = accessToken;
        else if (!TryReadAccessToken(databasePath ?? DatabasePath, out token, out var readFailure))
            return Fail(readFailure, queriedAt);
        if (!TrySessionCookie(token, DateTimeOffset.UtcNow, out var cookie, out var cookieFailure))
            return Fail(cookieFailure, queriedAt);

        var client = handler == null ? Http : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var summary = await Send(client, HttpMethod.Get, "https://cursor.com/api/usage-summary", cookie, null, cancellation);
            if (summary.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Fail("Cursor 登录已失效，请在 Cursor 中重新登录后再查。", queriedAt);
            if (!summary.IsSuccessStatusCode)
                return Fail("Cursor 额度查询没有成功。", queriedAt);
            return Parse(await summary.Content.ReadAsStringAsync(cancellation), queriedAt);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Fail("Cursor 额度查询超时。", queriedAt);
        }
        catch (HttpRequestException)
        {
            return Fail("连不上 Cursor，额度没查成。", queriedAt);
        }
        finally
        {
            if (!ReferenceEquals(client, Http)) client.Dispose();
        }
    }

    internal static bool TryReadAccessToken(string path, out string token, out string failure)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            failure = "未找到本机 Cursor 登录。请先打开 Cursor 并登录。";
            return false;
        }
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", "cursorAuth/accessToken");
            token = command.ExecuteScalar() as string ?? "";
        }
        catch (SqliteException)
        {
            failure = "暂时读不到 Cursor 的登录状态。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            failure = "未找到本机 Cursor 登录。请先打开 Cursor 并登录。";
            return false;
        }
        failure = "";
        return true;
    }

    internal static bool TrySessionCookie(string accessToken, DateTimeOffset now, out string cookie, out string failure)
    {
        cookie = "";
        var token = accessToken.Trim();
        if (token.StartsWith("cursor-safe-storage:", StringComparison.Ordinal))
        {
            failure = "Cursor 登录已加密，鱼鱼读不到。请在 Cursor 中保持登录后再查。";
            return false;
        }
        var separator = token.LastIndexOf("::", StringComparison.Ordinal);
        if (separator >= 0) token = token[(separator + 2)..];
        var encoded = token.LastIndexOf("%3A%3A", StringComparison.OrdinalIgnoreCase);
        if (encoded >= 0) token = token[(encoded + 6)..];
        if (!TryPayload(token, out var payload))
        {
            failure = "读到的 Cursor 登录无法用于查询。";
            return false;
        }
        if (!payload.TryGetProperty("exp", out var expiration) || !expiration.TryGetInt64(out var exp))
        {
            failure = "Cursor 登录信息缺少有效期。";
            return false;
        }
        if (DateTimeOffset.FromUnixTimeSeconds(exp) <= now.AddSeconds(60))
        {
            failure = "Cursor 登录已过期，请重新登录后再查。";
            return false;
        }
        if (!payload.TryGetProperty("sub", out var subject) || subject.GetString() is not { Length: > 0 } sub)
        {
            failure = "Cursor 登录信息缺少账户编号。";
            return false;
        }
        var user = sub;
        var bar = sub.LastIndexOf('|');
        if (bar >= 0) user = sub[(bar + 1)..];
        if (user.Length == 0 || user.Length > 128 || !IsUserId(user))
        {
            failure = "Cursor 登录信息里的账户编号无法使用。";
            return false;
        }
        cookie = "WorkosCursorSessionToken=" + user + "%3A%3A" + token;
        failure = "";
        return true;
    }

    internal static CursorQuotaReport Parse(string summaryJson, DateTimeOffset queriedAt)
    {
        try
        {
            using var summary = JsonDocument.Parse(summaryJson);
            if (summary.RootElement.ValueKind != JsonValueKind.Object)
                return Fail("Cursor 返回的额度数据无法识别。", queriedAt);
            var root = summary.RootElement;
            var lines = new List<CursorQuotaLine>();
            var cycleEnd = root.TryGetProperty("billingCycleEnd", out var end) && end.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var reset)
                ? reset : (DateTimeOffset?)null;
            if (root.TryGetProperty("individualUsage", out var individual) && individual.ValueKind == JsonValueKind.Object
                && individual.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                AddPercent(lines, "Auto", plan, "autoPercentUsed", cycleEnd);
                AddPercent(lines, "API", plan, "apiPercentUsed", cycleEnd);
            }
            var membership = Membership(root);
            var message = lines.Count == 0
                ? "已读取 Cursor 账户，但没有可显示的额度。"
                : string.IsNullOrEmpty(membership) ? "已读取 Cursor 账户。" : "已读取 Cursor 账户（" + membership + "）。";
            return new CursorQuotaReport(true, message, lines.ToArray(), queriedAt);
        }
        catch (JsonException)
        {
            return Fail("Cursor 返回的额度数据无法识别。", queriedAt);
        }
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string cookie, string? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, cancellation);
    }

    private static void AddPercent(List<CursorQuotaLine> lines, string title, JsonElement plan, string percentName, DateTimeOffset? reset)
    {
        if (TryNumber(plan, percentName, out var percent))
            lines.Add(new CursorQuotaLine(title, Remaining(percent), reset, null));
    }

    private static double Remaining(double usedPercent)
    {
        var used = Math.Clamp(usedPercent, 0, 100);
        return Math.Round(100 - used, 1);
    }

    private static string Membership(JsonElement root)
    {
        if (!root.TryGetProperty("membershipType", out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        var text = value.GetString()?.Trim().ToLowerInvariant() ?? "";
        if (text.Length == 0 || text.Length > 32) return "";
        foreach (var character in text)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '+')
                return "";
        return text;
    }

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value);
    }

    private static bool TryPayload(string jwt, out JsonElement payload)
    {
        payload = default;
        var parts = jwt.Split('.');
        if (parts.Length < 3 || parts[1].Length == 0) return false;
        var encoded = parts[1].Replace('-', '+').Replace('_', '/');
        switch (encoded.Length % 4)
        {
            case 2: encoded += "=="; break;
            case 3: encoded += "="; break;
            case 1: return false;
        }
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            payload = document.RootElement.Clone();
            return true;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool IsUserId(string user)
    {
        foreach (var character in user)
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                return false;
        return true;
    }

    private static CursorQuotaReport Fail(string message, DateTimeOffset queriedAt) =>
        new(false, message, [], queriedAt);
}
