using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BigBlueFish.Windows;
using Microsoft.Data.Sqlite;

internal static class CursorQuotaVerification
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }

    internal static async Task<int> Run(bool live)
    {
        var jwt = Jwt(new { sub = "auth0|user_123", exp = 9_999_999_999L });
        Check(CompanionCursorQuota.TrySessionCookie(jwt, DateTimeOffset.UnixEpoch, out var cookie, out _), "usable login becomes a session cookie");
        Check(cookie.StartsWith("WorkosCursorSessionToken=user_123%3A%3A", StringComparison.Ordinal) && cookie.EndsWith(jwt, StringComparison.Ordinal), "cookie keeps the account id and token");
        Check(!CompanionCursorQuota.TrySessionCookie(jwt, DateTimeOffset.FromUnixTimeSeconds(9_999_999_999), out _, out var expired) && expired.Contains("过期", StringComparison.Ordinal), "expired login is refused");
        Check(!CompanionCursorQuota.TrySessionCookie("cursor-safe-storage:v1:abc", DateTimeOffset.UnixEpoch, out _, out var encrypted) && encrypted.Contains("加密", StringComparison.Ordinal), "encrypted login is refused");
        Check(!CompanionCursorQuota.TrySessionCookie("not-a-token", DateTimeOffset.UnixEpoch, out _, out _), "a non-token login is refused");

        var database = Path.Combine(Path.GetTempPath(), "cursor-quota-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var connection = new SqliteConnection("Data Source=" + database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE ItemTable (key TEXT, value TEXT); INSERT INTO ItemTable (key, value) VALUES ('cursorAuth/accessToken', $token);";
                command.Parameters.AddWithValue("$token", jwt);
                command.ExecuteNonQuery();
            }
            Check(CompanionCursorQuota.TryReadAccessToken(database, out var stored, out _) && stored == jwt, "login is read from the local database");
            Check(!CompanionCursorQuota.TryReadAccessToken(database + ".missing", out _, out var missing) && missing.Contains("未找到", StringComparison.Ordinal), "missing database explains that Cursor is not logged in");
        }
        finally { try { File.Delete(database); } catch (IOException) { } }

        var summary = """
            {"billingCycleEnd":"2026-11-02T14:11:55.000Z","membershipType":"pro","isUnlimited":false,"individualUsage":{"plan":{"enabled":true,"used":125,"limit":1000,"totalPercentUsed":12.5,"autoPercentUsed":3,"apiPercentUsed":40},"onDemand":{"enabled":true,"used":2309,"limit":null}}}
            """;
        var sand = """{"usagePercent":20,"nextResetTimestampUtc":"2026-11-03T00:00:00.000Z"}""";
        var parsed = CompanionCursorQuota.Parse(summary, DateTimeOffset.Now);
        Check(parsed.Success && parsed.Message.Contains("pro", StringComparison.Ordinal), "summary names the membership");
        Check(parsed.Lines.Select(line => line.Title).SequenceEqual(new[] { "Auto", "API" }), "cursor quota keeps only Auto and API");
        Check(Line(parsed, "Auto")?.RemainingPercent == 97 && Line(parsed, "API")?.RemainingPercent == 60, "auto and api percents stay separate");
        Check(!parsed.Message.Contains(jwt, StringComparison.Ordinal), "the report does not contain the login token");

        var withoutPercents = CompanionCursorQuota.Parse("""{"isUnlimited":true,"membershipType":"pro_plus","individualUsage":{"plan":{"totalPercentUsed":0},"onDemand":{"used":2309}}}""", DateTimeOffset.Now);
        Check(withoutPercents.Success && withoutPercents.Lines.Length == 0, "plan total and on-demand are not shown");
        Check(CompanionCursorQuota.Parse("[]", DateTimeOffset.Now) is { Success: false }, "a non-object response is not treated as zero usage");
        Check(CompanionCursorQuota.Parse("""{"individualUsage":{}}""", DateTimeOffset.Now) is { Success: true, Lines.Length: 0 }, "missing usage is not filled in as zero");

        var transport = new Probe(summary, sand, HttpStatusCode.OK);
        var queried = await CompanionCursorQuota.Query(transport, jwt, null, CancellationToken.None);
        Check(queried.Success && Line(queried, "Auto")?.RemainingPercent == 97 && queried.Lines.All(line => line.Title is "Auto" or "API"), "query reads only Auto and API");
        Check(transport.SawCookie && transport.SawOrigin && !queried.Message.Contains(jwt, StringComparison.Ordinal), "query sends the session without putting it in the result");
        var denied = await CompanionCursorQuota.Query(new Probe("{}", null, HttpStatusCode.Unauthorized), jwt, null, CancellationToken.None);
        Check(!denied.Success && denied.Message.Contains("失效", StringComparison.Ordinal) && !denied.Message.Contains(jwt, StringComparison.Ordinal), "a rejected login stays out of the message");

        if (live)
        {
            var report = await CompanionCursorQuota.Query();
            Console.WriteLine(report.Success ? "LIVE " + report.Message : "LIVE-FAIL " + report.Message);
            foreach (var line in report.Lines)
                Console.WriteLine("LIVE " + line.Title + " " + (line.RemainingPercent?.ToString() ?? line.Note ?? ""));
            Check(report.Success && report.Lines.Length > 0 && report.Lines.All(line => line.Title is "Auto" or "API"), "live Cursor account query shows only Auto and API");
        }
        Console.WriteLine("CURSOR-QUOTA-OK " + checks);
        return 0;
    }

    private static CursorQuotaLine? Line(CursorQuotaReport report, string title) =>
        report.Lines.FirstOrDefault(line => line.Title == title);

    private static string Jwt(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    }

    private sealed class Probe : HttpMessageHandler
    {
        private readonly string summary;
        private readonly string? sand;
        private readonly HttpStatusCode status;
        internal bool SawCookie, SawOrigin;
        internal Probe(string summary, string? sand, HttpStatusCode status)
        {
            this.summary = summary;
            this.sand = sand;
            this.status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var values) ? values.FirstOrDefault() ?? "" : "";
            SawCookie |= cookie.StartsWith("WorkosCursorSessionToken=user_123%3A%3A", StringComparison.Ordinal) && cookie.Contains(".sig", StringComparison.Ordinal);
            SawOrigin |= request.Headers.TryGetValues("Origin", out var origin) && origin.Contains("https://cursor.com");
            var body = request.RequestUri?.AbsolutePath.Contains("usage-summary", StringComparison.Ordinal) == true ? summary : sand ?? "{}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
