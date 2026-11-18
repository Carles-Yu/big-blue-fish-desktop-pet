using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
/// Official cursor.exe and Agent CLI have no documented non-interactive usage command, so a
/// cursor-safe-storage:v1 token is unlocked with this Windows user's DPAPI OSCrypt key.
/// The session value, key, and ciphertext are never written to logs, chat, settings, or the window text.
/// </summary>
internal static class CompanionCursorQuota
{
    private const string SafeStoragePrefix = "cursor-safe-storage:";
    private const string SafeStorageV1Prefix = "cursor-safe-storage:v1:";
    private const string EncryptedUnreadMessage = "Cursor 登录已加密，鱼鱼读不到。请在 Cursor 中保持登录后再查。";
    private static readonly byte[] DpapiKeyPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] ChromiumV10 = "v10"u8.ToArray();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    internal static string LocalStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "Local State");

    internal static Task<CursorQuotaReport> Query(CancellationToken cancellation = default) =>
        Query(null, null, DatabasePath, LocalStatePath, cancellation);

    internal static Task<CursorQuotaReport> Query(
        HttpMessageHandler? handler, string? accessToken, string? databasePath, CancellationToken cancellation = default) =>
        Query(handler, accessToken, databasePath, LocalStatePath, cancellation);

    internal static async Task<CursorQuotaReport> Query(
        HttpMessageHandler? handler, string? accessToken, string? databasePath, string? localStatePath, CancellationToken cancellation = default)
    {
        var queriedAt = DateTimeOffset.Now;
        string token;
        if (accessToken != null)
        {
            if (!TryUnlockAccessToken(accessToken, localStatePath, out token, out var unlockFailure))
                return Fail(unlockFailure, queriedAt);
        }
        else if (!TryReadAccessToken(databasePath ?? DatabasePath, localStatePath ?? LocalStatePath, out token, out var readFailure))
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

    internal static bool TryReadAccessToken(string path, out string token, out string failure) =>
        TryReadAccessToken(path, LocalStatePath, out token, out failure);

    internal static bool TryReadAccessToken(string path, string? localStatePath, out string token, out string failure)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            failure = "未找到本机 Cursor 登录。请先打开 Cursor 并登录。";
            return false;
        }
        string stored;
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
            stored = command.ExecuteScalar() as string ?? "";
        }
        catch (SqliteException)
        {
            failure = "暂时读不到 Cursor 的登录状态。";
            token = "";
            return false;
        }
        if (string.IsNullOrWhiteSpace(stored))
        {
            failure = "未找到本机 Cursor 登录。请先打开 Cursor 并登录。";
            return false;
        }
        return TryUnlockAccessToken(stored, localStatePath, out token, out failure);
    }

    internal static bool TryUnlockAccessToken(string stored, string? localStatePath, out string token, out string failure, byte[]? encryptionKey = null)
    {
        token = "";
        failure = "";
        if (string.IsNullOrWhiteSpace(stored))
        {
            failure = "未找到本机 Cursor 登录。请先打开 Cursor 并登录。";
            return false;
        }
        var value = stored.Trim();
        if (!value.StartsWith(SafeStoragePrefix, StringComparison.Ordinal))
        {
            token = value;
            return true;
        }
        if (!value.StartsWith(SafeStorageV1Prefix, StringComparison.Ordinal))
        {
            failure = "Cursor 登录的加密方式已更新，暂时无法读取。请更新蓝色大肥鱼后再查。";
            return false;
        }
        byte[]? loadedKey = null;
        if (encryptionKey == null)
        {
            if (!TryReadOsCryptKey(localStatePath ?? LocalStatePath, out loadedKey, out failure))
                return false;
            encryptionKey = loadedKey;
        }
        try
        {
            var payload = Convert.FromBase64String(value[SafeStorageV1Prefix.Length..].Trim());
            if (payload.Length < 31 || !payload.AsSpan().StartsWith(ChromiumV10))
            {
                failure = EncryptedUnreadMessage;
                return false;
            }
            var nonce = payload.AsSpan(3, 12);
            var tag = payload.AsSpan(payload.Length - 16, 16);
            var ciphertext = payload.AsSpan(15, payload.Length - 31);
            var plain = new byte[ciphertext.Length];
            using (var gcm = new AesGcm(encryptionKey, 16))
                gcm.Decrypt(nonce, ciphertext, tag, plain);
            token = Encoding.UTF8.GetString(plain).Trim();
            CryptographicOperations.ZeroMemory(plain);
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith(SafeStoragePrefix, StringComparison.Ordinal))
            {
                token = "";
                failure = EncryptedUnreadMessage;
                return false;
            }
            return true;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException or DecoderFallbackException)
        {
            failure = EncryptedUnreadMessage;
            token = "";
            return false;
        }
        finally
        {
            if (loadedKey != null)
                CryptographicOperations.ZeroMemory(loadedKey);
        }
    }

    internal static bool TryReadOsCryptKey(string localStatePath, out byte[] key, out string failure)
    {
        key = [];
        failure = EncryptedUnreadMessage;
        if (string.IsNullOrWhiteSpace(localStatePath) || !File.Exists(localStatePath))
        {
            failure = "Cursor 登录已加密，但找不到本机解锁文件。请在 Cursor 中保持登录后再查。";
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt)
                || !osCrypt.TryGetProperty("encrypted_key", out var encrypted)
                || encrypted.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(encrypted.GetString()))
            {
                failure = EncryptedUnreadMessage;
                return false;
            }
            var blob = Convert.FromBase64String(encrypted.GetString()!);
            if (blob.Length <= DpapiKeyPrefix.Length || !blob.AsSpan().StartsWith(DpapiKeyPrefix))
            {
                failure = EncryptedUnreadMessage;
                return false;
            }
            key = UnprotectCurrentUser(blob.AsSpan(DpapiKeyPrefix.Length).ToArray());
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                key = [];
                failure = EncryptedUnreadMessage;
                return false;
            }
            failure = "";
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            failure = "Cursor 登录已加密，鱼鱼无法在当前 Windows 账户下解锁。请用登录 Cursor 的同一账户运行鱼鱼后再查。";
            key = [];
            return false;
        }
    }

    internal static string EncryptAccessToken(string token, byte[] key)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, ciphertext, tag);
            var payload = new byte[3 + nonce.Length + ciphertext.Length + tag.Length];
            ChromiumV10.CopyTo(payload, 0);
            nonce.CopyTo(payload, 3);
            ciphertext.CopyTo(payload, 15);
            tag.CopyTo(payload, 15 + ciphertext.Length);
            return SafeStorageV1Prefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static void WriteLocalState(string path, byte[] key)
    {
        var protectedKey = ProtectCurrentUser(key);
        var blob = new byte[DpapiKeyPrefix.Length + protectedKey.Length];
        DpapiKeyPrefix.CopyTo(blob, 0);
        protectedKey.CopyTo(blob, DpapiKeyPrefix.Length);
        File.WriteAllText(path, "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String(blob) + "\"}}");
        CryptographicOperations.ZeroMemory(protectedKey);
    }

    internal static bool TrySessionCookie(string accessToken, DateTimeOffset now, out string cookie, out string failure)
    {
        cookie = "";
        var token = accessToken.Trim();
        if (token.StartsWith(SafeStoragePrefix, StringComparison.Ordinal))
        {
            failure = EncryptedUnreadMessage;
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

    internal static byte[] ProtectCurrentUser(byte[] data) => ProtectOrUnprotect(data, protect: true);
    internal static byte[] UnprotectCurrentUser(byte[] data) => ProtectOrUnprotect(data, protect: false);

    private static byte[] ProtectOrUnprotect(byte[] data, bool protect)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = pin.AddrOfPinnedObject() };
            var output = new DataBlob();
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, (int)output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
