using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BigBlueFish.Windows;

internal static class CodexQuotaVerification
{
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private const string Valid = """{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1793105176},"secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1793601512}}}}""";

    internal static async Task<int> Run(string directory, bool live)
    {
        Directory.CreateDirectory(directory);
        if (live)
        {
            var report = await CompanionCodexQuota.Query();
            Check(report.Success && report.Buckets.Length > 0, "Real native Codex account query succeeds without a model turn");
            foreach (var bucket in report.Buckets)
                foreach (var w in bucket.Windows) Console.WriteLine($"WINDOW duration_minutes={w.WindowMinutes}, remaining_percent={w.RemainingPercent}");
            return 0;
        }
        var now = DateTimeOffset.UtcNow;
        var parsed = CompanionCodexQuota.Parse(Json(Valid), now);
        Check(parsed.Buckets.Length == 1 && parsed.Buckets[0].Windows[0].RemainingPercent == 88, "Prefer bucket map and convert used to remaining");
        Check(parsed.Buckets[0].Windows[1].RemainingPercent == 0, "Actual exhaustion remains zero");
        Check(parsed.Buckets[0].Windows[0].DurationLabel == "5 小时" && parsed.Buckets[0].Windows[1].DurationLabel == "每周", "Window labels follow actual durations");
        var legacy = CompanionCodexQuota.Parse(Json("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":21,"windowDurationMins":60}}}"""), now);
        Check(legacy.Buckets.Single().Windows.Single().RemainingPercent == 79, "Legacy single bucket fallback");
        var missing = CompanionCodexQuota.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":null,"resetsAt":null}}}"""), now);
        Check(missing.Buckets[0].Windows[0].RemainingPercent == null && missing.Buckets[0].Windows[0].ResetsAt == null, "Unknown values are not zero or fabricated reset times");
        var empty = CompanionCodexQuota.Parse(Json("{}"), now);
        Check(empty.Buckets.Length == 0 && empty.Message.Contains("未知"), "Missing quota is explicitly unknown");
        var multiple = CompanionCodexQuota.Parse(Json("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":110}},"other":{"limitName":"Other bucket","primary":{"usedPercent":-10,"resetsAt":9223372036854775807}}}}"""), now);
        Check(multiple.Buckets.Length == 2 && multiple.Buckets[0].Windows[0].RemainingPercent == 0 && multiple.Buckets[1].Windows[0].RemainingPercent == 100
            && multiple.Buckets[1].Windows[0].ResetsAt == null, "Clamp percentages and tolerate invalid timestamps across multiple buckets");

        var previousExe = Environment.GetEnvironmentVariable("BIGBLUEFISH_CODEX_PATH");
        var previousMode = Environment.GetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE");
        var previousLog = Environment.GetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE_LOG");
        try
        {
            Environment.SetEnvironmentVariable("BIGBLUEFISH_CODEX_PATH", Environment.ProcessPath);
            var log = Path.GetFullPath(Path.Combine(directory, "rpc-methods.txt"));
            File.WriteAllText(log, "");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE_LOG", log);
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", "valid");
            var report = await CompanionCodexQuota.Query();
            Check(report.Success && report.Buckets[0].Windows[0].RemainingPercent == 88, "Native-process JSON-RPC handshake and quota read");
            var methods = File.ReadAllLines(log);
            Check(methods.SequenceEqual(new[] { "initialize", "initialized", "account/rateLimits/read" }), "No thread, prompt, login, or model request is sent");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", "auth");
            report = await CompanionCodexQuota.Query();
            Check(!report.Success && report.Message.Contains("登录") && !report.Message.Contains("secret-sentinel"), "Authentication failure is useful and sanitized");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", "unsupported");
            report = await CompanionCodexQuota.Query();
            Check(!report.Success && report.Message.Contains("更新"), "Old server gets update guidance");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", "malformed");
            report = await CompanionCodexQuota.Query();
            Check(!report.Success && report.Message.Contains("格式"), "Malformed JSON is not treated as success");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", "wait");
            using var cancel = new CancellationTokenSource(500);
            bool cancelled = false;
            try { await CompanionCodexQuota.Query(cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Window-close cancellation terminates the pending read");
            Environment.SetEnvironmentVariable("BIGBLUEFISH_CODEX_PATH", Path.Combine(directory, "missing-codex.exe"));
            report = await CompanionCodexQuota.Query();
            Check(!report.Success && report.Message.Contains("未找到"), "Missing executable has actionable guidance");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BIGBLUEFISH_CODEX_PATH", previousExe);
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE", previousMode);
            Environment.SetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE_LOG", previousLog);
        }
        VerifyUi(directory, parsed);
        Console.WriteLine("CODEX-QUOTA-OK " + checks); return 0;
    }

    internal static async Task<int> Fixture()
    {
        var mode = Environment.GetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE");
        if (mode == null) return 1;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            var request = Json(line); var method = request.GetProperty("method").GetString();
            File.AppendAllText(Environment.GetEnvironmentVariable("BIGBLUEFISH_QUOTA_FIXTURE_LOG")!, method + "\n");
            if (!request.TryGetProperty("id", out var id)) continue;
            if (method == "initialize") { Console.WriteLine(JsonSerializer.Serialize(new { id = id.Clone(), result = new { } })); continue; }
            if (mode == "wait") { await Task.Delay(Timeout.Infinite); return 0; }
            if (mode == "malformed") { Console.WriteLine("not-json secret-sentinel"); continue; }
            if (mode is "auth" or "unsupported")
                Console.WriteLine(JsonSerializer.Serialize(new { id = id.Clone(), error = new { code = mode == "auth" ? -32000 : -32601, message = "secret-sentinel" } }));
            else
            {
                await Console.Error.WriteAsync(new string('x', 100000)); // stderr must be drained without buffering/printing credentials.
                Console.WriteLine(JsonSerializer.Serialize(new { id = id.Clone(), result = Json(Valid) }));
            }
        }
        return 0;
    }

    private static void VerifyUi(string directory, CodexQuotaReport report)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CompanionCodexQuotaWindow? window = null;
            CompanionCursorQuotaWindow? cursorWindow = null;
            void Pump()
            {
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            }
            IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i); if (child is T t) yield return t;
                    foreach (var nested in Children<T>(child)) yield return nested;
                }
            }
            try
            {
                int calls = 0;
                window = new CompanionCodexQuotaWindow(_ => Task.FromResult(++calls == 1 ? report : new CodexQuotaReport(false, "离线测试：暂时无法获取", [], DateTimeOffset.UtcNow)));
                window.Show(); Pump();
                Check(Children<TextBlock>(window).Any(t => t.Text.Contains("剩余 88%")), "Actual WPF card shows remaining quota");
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(directory, "codex-quota.png"))) encoder.Save(file);
                var refresh = Children<System.Windows.Controls.Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "codex-quota-refresh");
                refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                Check(Children<TextBlock>(window).Any(t => t.Text.Contains("尚未刷新")) && Children<TextBlock>(window).Any(t => t.Text.Contains("剩余 88%")), "Failed refresh preserves and labels old data");
                window.Close();
                var cursorReport = new CursorQuotaReport(true, "Cursor pro", [
                    new CursorQuotaLine("Auto", 97, DateTimeOffset.Now.AddDays(4), null),
                    new CursorQuotaLine("API", 16, DateTimeOffset.Now.AddDays(4), null)], DateTimeOffset.Now);
                cursorWindow = new CompanionCursorQuotaWindow(_ => Task.FromResult(cursorReport));
                cursorWindow.Show(); Pump();
                Check(Children<TextBlock>(cursorWindow).Any(t => t.Text.Contains("剩余 97%")) && Children<TextBlock>(cursorWindow).Any(t => t.Text == "余量较低"), "Cursor shared cards retain separate values and low-quota state");
                var cursorContent = (FrameworkElement)cursorWindow.Content;
                var cursorBitmap = new RenderTargetBitmap((int)cursorContent.ActualWidth, (int)cursorContent.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var cursorDrawing = new DrawingVisual();
                using (var context = cursorDrawing.RenderOpen()) context.DrawRectangle(new VisualBrush(cursorContent), null, new Rect(0, 0, cursorContent.ActualWidth, cursorContent.ActualHeight));
                cursorBitmap.Render(cursorDrawing); var cursorEncoder = new PngBitmapEncoder(); cursorEncoder.Frames.Add(BitmapFrame.Create(cursorBitmap));
                using (var file = File.Create(Path.Combine(directory, "cursor-quota.png"))) cursorEncoder.Save(file);
                Check(CompanionBrandIcons.Source(CompanionBrand.Cursor).Width > 0 && CompanionBrandIcons.Source(CompanionBrand.Codex).Width > 0
                    && CompanionBrandIcons.Tray(CompanionBrand.Cursor).Width == 64 && CompanionBrandIcons.Tray(CompanionBrand.Codex).Width == 64,
                    "Bundled provider icons load for WPF and native tray renderer");
            }
            catch (Exception error) { failure = error; }
            finally { cursorWindow?.Close(); window?.Close(); app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
