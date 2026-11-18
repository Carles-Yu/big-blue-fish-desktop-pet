using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using BigBlueFish.Windows;

internal static class GeminiQuotaVerification
{
    private static int checks;
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        Console.WriteLine("PASS " + description);
        checks++;
    }

    internal const string UsageJson = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","description":"Gemini model usage","buckets":[{"id":"gemini-weekly","name":"Weekly Gemini limit","window":"weekly","description":"Weekly window","remaining_fraction":0.73,"reset_time":"2026-11-24T05:45:50Z"},{"id":"gemini-5h","name":"5h Gemini limit","window":"5h","description":"Five-hour window","remaining_fraction":0.41,"reset_time":"2026-11-17T10:00:00Z"}]},{"name":"Claude and GPT models","description":"Third-party model usage","buckets":[{"id":"3p-weekly","name":"Weekly third-party limit","window":"weekly","remaining_fraction":0.0,"reset_time":"2026-11-24T05:45:50Z"},{"id":"3p-5h","name":"5h third-party limit","window":"5h","remaining_fraction":0.95,"reset_time":"2026-11-17T10:00:00Z"}]}]}}}
        """;

    internal const string CreditsJson = """
        {"status":"SUCCESS","command":{"name":"credits","data":{"remaining_credits":128,"upgrade_uri":"https://antigravity.google/g1-upgrade"}}}
        """;

    internal const string CreditsZeroJson = """
        {"status":"SUCCESS","command":{"name":"credits","data":{"remaining_credits":0,"upgrade_uri":"https://antigravity.google/g1-upgrade"}}}
        """;

    internal const string CreditsMissingJson = """
        {"status":"SUCCESS","command":{"name":"credits","data":{"upgrade_uri":"https://antigravity.google/g1-upgrade"}}}
        """;

    internal const string UsageMissingFractionJson = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","reset_time":"2026-11-24T05:45:50Z"}]}]}}}
        """;

    internal const string ErrorJson = """
        {"status":"ERROR","command":{"name":"usage","error":{"message":"RAW_COMMAND_ERROR should never appear"}}}
        """;

    internal static int Fixture(string[] args)
    {
        var mode = Environment.GetEnvironmentVariable("BIGBLUEFISH_GEMINI_QUOTA_FIXTURE") ?? "ok";
        string? prompt = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "-p" && i + 1 < args.Length) prompt = args[i + 1];
        bool credits = prompt == "/credits";
        if (mode == "wait") { Thread.Sleep(60_000); return 0; }
        if (mode == "crash") return 2;
        if (mode == "stderr") Console.Error.WriteLine("RAW_SECRET_OR_COMMAND_ERROR should never appear in UI");
        string json = mode switch
        {
            "ok" or "stderr" => credits ? CreditsJson : UsageJson,
            "credits-zero" => credits ? CreditsZeroJson : UsageJson,
            "credits-fail" => credits ? ErrorJson : UsageJson,
            "credits-missing" => credits ? CreditsMissingJson : UsageJson,
            "usage-fail" => credits ? CreditsJson : ErrorJson,
            "auth" => ErrorJson,
            "malformed" => "{not json",
            "missing-fraction" => credits ? CreditsJson : UsageMissingFractionJson,
            _ => ErrorJson
        };
        Console.Out.Write(json);
        return 0;
    }

    internal static async Task<int> Run(bool live)
    {
        var usageBuckets = new List<GeminiQuotaBucket>();
        Check(CompanionGeminiQuota.TryParseUsage(UsageJson, usageBuckets, out var usageError) && usageError == null, "sanitized usage schema parses");
        Check(usageBuckets.Count == 4, "usage exposes weekly and 5h buckets for both model groups");
        Check(usageBuckets[0] is { Group: "Gemini Models", Title: "周限额", RemainingPercent: 73 }, "Gemini weekly remaining is a percentage");
        Check(usageBuckets[1] is { Title: "5 小时限额", RemainingPercent: 41 }, "Gemini 5h remaining is a percentage");
        Check(usageBuckets[2] is { Group: "Claude and GPT models", Title: "周限额", RemainingPercent: 0 }, "zero remaining stays zero, not unknown");
        Check(usageBuckets[3] is { Title: "5 小时限额", RemainingPercent: 95 }, "third-party 5h remaining is a percentage");
        Check(usageBuckets[0].ResetsAt == DateTimeOffset.Parse("2026-11-24T05:45:50Z"), "reset timestamps stay in UTC for local display");
        var credits = CompanionGeminiQuota.ParseCredits(CreditsJson);
        Check(credits is { Group: "积分", Title: "积分", RemainingPercent: null, Note: "剩余 128 积分" }, "credits are a count card, not a percentage");
        Check(CompanionGeminiQuota.ParseCredits(CreditsZeroJson) is { Note: "剩余 0 积分", RemainingPercent: null }, "zero credits stay zero, not unknown");
        Check(CompanionGeminiQuota.ParseCredits(CreditsMissingJson) is { Note: null, RemainingPercent: null }, "missing credits display as unknown, not zero");
        var missing = new List<GeminiQuotaBucket>();
        Check(CompanionGeminiQuota.TryParseUsage(UsageMissingFractionJson, missing, out _) && missing[0].RemainingPercent is null, "missing remaining_fraction displays as unknown");
        Check(!CompanionGeminiQuota.TryParseUsage(ErrorJson, new List<GeminiQuotaBucket>(), out var errorMessage)
            && errorMessage == "未能读取额度。请先安装并登录 agy 后重试。"
            && errorMessage.Contains("RAW_COMMAND_ERROR") == false, "command errors stay generic and omit raw text");
        try { CompanionGeminiQuota.TryParseUsage("{not json", new List<GeminiQuotaBucket>(), out _); Check(false, "malformed JSON should throw"); }
        catch (JsonException) { Check(true, "malformed JSON throws a format error"); }

        Check(CompanionGeminiQuota.ResolveExecutable("agy.exe", [@"C:\Windows"], @"C:\missing\agy.exe", out var relativeError) == null
            && relativeError == CompanionGeminiQuota.InvalidPathMessage, "relative configured path is rejected");
        Check(CompanionGeminiQuota.ResolveExecutable(@"C:\missing-agy-path\agy.exe", [@"C:\Windows"], @"C:\missing\agy.exe", out var missingError) == null
            && missingError == CompanionGeminiQuota.InvalidPathMessage, "missing configured path is rejected");
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Check(File.Exists(notepad)
            && CompanionGeminiQuota.ResolveExecutable(notepad, [@"C:\Windows"], notepad, out var nameError) == null
            && nameError == CompanionGeminiQuota.InvalidPathMessage, "configured path that is not agy.exe does not fall back");
        Check(CompanionGeminiQuota.ResolveExecutable(null, Array.Empty<string>(), null, out var notFound) == null
            && notFound == CompanionGeminiQuota.NotFoundMessage, "missing agy reports a specific not-found message");

        var directory = Path.Combine(Path.GetTempPath(), "bigbluefish-gemini-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fixture = InstallFixtureAgy(directory);
            Check(CompanionGeminiQuota.ResolveExecutable(fixture, Array.Empty<string>(), null, out var okError) == Path.GetFullPath(fixture) && okError == null, "valid agy.exe path is accepted");
            Check(CompanionGeminiQuota.ResolveExecutable(null, [directory], null, out var pathError) == fixture && pathError == null, "PATH lookup finds agy.exe before the default");
            var defaultDir = Path.Combine(directory, "default");
            Directory.CreateDirectory(defaultDir);
            var defaultAgy = Path.Combine(defaultDir, "agy.exe");
            File.Copy(fixture, defaultAgy, true);
            CopySidecars(fixture, defaultAgy);
            Check(CompanionGeminiQuota.ResolveExecutable(null, Array.Empty<string>(), defaultAgy, out var defaultError) == Path.GetFullPath(defaultAgy) && defaultError == null, "user-profile default agy.exe is used when PATH has none");
            Check(CompanionGeminiQuota.ResolveExecutable(@"C:\missing-agy-path\agy.exe", [directory], defaultAgy, out var noFallback) == null
                && noFallback == CompanionGeminiQuota.InvalidPathMessage, "invalid explicit path does not fall back to PATH or default");

            var ok = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("ok"));
            Check(ok.Success && ok.Buckets.Length == 5 && ok.Message.Contains("已读取") && !ok.Message.Contains("128"), "process fixture returns usage groups plus credits");
            Check(ok.Buckets[0] is { Group: "Gemini Models", Title: "周限额", RemainingPercent: 73 }, "process usage matches sanitized weekly fraction");
            Check(ok.Buckets[^1] is { Note: "剩余 128 积分", RemainingPercent: null }, "process credits card keeps a count note");
            var view = CompanionGeminiQuota.ToView(ok);
            Check(view.Cards.Length == 5 && view.Cards[^1].Note == "剩余 128 积分" && view.Cards[^1].Remaining is null, "shared cards keep credits off the percent scale");

            var creditsFail = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("credits-fail"));
            Check(creditsFail.Success && creditsFail.Buckets.Any(bucket => bucket is { Group: "Gemini Models", RemainingPercent: 73 })
                && creditsFail.Buckets[^1] is { Title: "积分", RemainingPercent: null, Note: null }, "credits failure still shows usage and unknown credits");

            var zero = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("credits-zero"));
            Check(zero.Buckets[^1].Note == "剩余 0 积分", "live-shaped zero credits stay a real zero");

            var stderr = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("stderr"));
            Check(stderr.Success && !stderr.Message.Contains("RAW_SECRET") && stderr.Buckets.All(bucket => bucket.Note is null || !bucket.Note.Contains("RAW_SECRET")), "stderr is drained and never shown");

            var auth = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("auth"));
            Check(!auth.Success && auth.Message == "未能读取额度。请先安装并登录 agy 后重试。"
                && !auth.Message.Contains("RAW_COMMAND_ERROR"), "login-style errors stay generic");

            using var cancel = new CancellationTokenSource();
            var pending = CompanionGeminiQuota.Query(fixture, cancel.Token, Env("wait"));
            cancel.Cancel();
            var cancelled = await pending;
            Check(!cancelled.Success && cancelled.Message == "查询已取消。", "closing the panel cancels the query");

            var timeoutWatch = Stopwatch.StartNew();
            var timedOut = await CompanionGeminiQuota.Query(fixture, CancellationToken.None, Env("wait"));
            timeoutWatch.Stop();
            Check(!timedOut.Success && timedOut.Message == "查询超时，请稍后重试。"
                && timeoutWatch.Elapsed < TimeSpan.FromSeconds(45), "query times out in about 30 seconds and reaps the child");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }

        Exception? iconError = null;
        System.Windows.Media.ImageSource? geminiIcon = null;
        System.Drawing.Bitmap? tray = null;
        var iconThread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) _ = new System.Windows.Application();
                geminiIcon = CompanionBrandIcons.Source(CompanionBrand.Gemini);
                tray = CompanionBrandIcons.Tray(CompanionBrand.Gemini);
            }
            catch (Exception error) { iconError = error; }
        });
        iconThread.SetApartmentState(ApartmentState.STA);
        iconThread.Start();
        iconThread.Join();
        if (iconError is not null) throw iconError;
        Check(geminiIcon != null && geminiIcon.Width > 0 && geminiIcon.Height > 0, "official Gemini mark loads as a WPF image");
        using (tray)
            Check(tray != null && tray.Width == 64 && tray.Height == 64, "Gemini tray bitmap renders at the shared icon size");

        if (live)
        {
            var liveReport = await CompanionGeminiQuota.Query();
            Check(liveReport.Success, "live official agy query succeeds");
            Check(liveReport.Buckets.Length > 0, "live query returns at least one card");
            Console.WriteLine("LIVE official agy query succeeded; percentages and credit counts are snapshots and are not recorded.");
        }

        Console.WriteLine($"RESULT {checks} Gemini quota checks passed");
        return 0;
    }

    private static IReadOnlyDictionary<string, string?> Env(string mode) =>
        new Dictionary<string, string?> { ["BIGBLUEFISH_GEMINI_QUOTA_FIXTURE"] = mode };

    private static string InstallFixtureAgy(string directory)
    {
        var sourceDir = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        var dest = Path.Combine(directory, "agy.exe");
        File.Copy(Environment.ProcessPath!, dest, true);
        CopySidecars(Environment.ProcessPath!, dest);
        return dest;
    }

    private static void CopySidecars(string sourceExe, string destExe)
    {
        foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json", ".pdb" })
        {
            var source = Path.ChangeExtension(sourceExe, extension);
            if (File.Exists(source))
                File.Copy(source, Path.ChangeExtension(destExe, extension), true);
        }
    }
}
