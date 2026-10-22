using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BigBlueFish.Windows;
using Button = System.Windows.Controls.Button;

internal static class CloudQuietVerification
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    static HttpResponseMessage Sse(string text = "主人好～") => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    static HttpResponseMessage WeatherResponse() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
        current = new { time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-ddTHH:mm"), temperature_2m = 20, apparent_temperature = 20, relative_humidity_2m = 50, weather_code = 0, wind_speed_10m = 5 },
        daily = new { temperature_2m_min = new[] { 15 }, temperature_2m_max = new[] { 25 }, precipitation_probability_max = new[] { 0 } }
    })) };
    static readonly CompanionCloud.Settings Configuration = new("https://fixture.invalid/v1", "fixture-secret", "fixture-model");
    static CompanionBrain Brain(HttpMessageHandler handler, Func<CompanionCloud.Settings>? config = null, HttpClient? weather = null) =>
        new(_ => throw new Exception("Cloud must never start a runner"), TimeSpan.FromSeconds(1), weatherClient: weather,
            cloudClient: new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }, cloudSettings: config ?? (() => Configuration)) { UseCloud = true, StopOnBattery = true };

    internal static async Task<int> Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            Config(directory);
            Quiet();
            CompanionNative.TestPowerOverride = false;
            var prompts = new List<string>();
            using (var brain = Brain(new Handler(async (request, token) => {
                var body = await request.Content!.ReadAsStringAsync(token);
                using var payload = JsonDocument.Parse(body); prompts.Add(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Check(request.Headers.Authorization?.Parameter == Configuration.ApiKey, "Bearer key reaches only the request header");
                Check(!body.Contains(Configuration.ApiKey) && !body.Contains("image_url"), "Cloud payload contains neither credentials nor image data");
                return Sse();
            })))
            {
                Check(brain.CanRunOnCurrentPower, "Local battery pause does not pause cloud");
                brain.StopCloudOnBattery = true; Check(!brain.CanRunOnCurrentPower, "Cloud has its own battery pause");
                brain.StopCloudOnBattery = false;
                var test = await brain.TestCloudConnection();
                Check(prompts.Single() == "请回复：你好。", "Connection test sends only the fixed test prompt");
                Check(test.Contains("成功") && brain.CloudResult(Configuration).Contains("成功"), "Connection test completes an actual request with a result");
                Check(brain.CloudResult(Configuration with { Model = "changed" }) == "尚未验证", "Edited configuration is unverified");
                string partial = "";
                Check(await brain.Generate(false, false, onPartial: text => partial = text) == "主人好～" && partial == "主人好～" && brain.RunnerPid == null, "Cloud generation works on battery without a local model");
                brain.UseCloud = false; Check(!brain.CanRunOnCurrentPower, "Local mode still pauses on battery");
            }
            CompanionNative.TestPowerOverride = true;
            foreach (var entry in new[] { (401, "鉴权"), (403, "鉴权"), (429, "限流"), (503, "不可用"), (400, "HTTP 400") })
            {
                int calls = 0;
                using var brain = Brain(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)entry.Item1) { Content = new StringContent(Configuration.ApiKey) }); }));
                Check(await brain.Generate(false, false) == "" && brain.Status.Contains(entry.Item2), "HTTP " + entry.Item1 + " gives an actionable error");
                Check(calls == 1 && brain.UseCloud && !brain.Status.Contains(Configuration.ApiKey), "Failure does not retry, downgrade or echo credentials");
            }
            foreach (var entry in new[] { ("{bad-json", "格式"), ("data: {}\n\ndata: [DONE]\n", "为空"), ("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n", "中断") })
            {
                using var brain = Brain(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(entry.Item1) })));
                Check(await brain.Generate(false, false) == "" && brain.Status.Contains(entry.Item2), "Malformed or interrupted response is not reported as success");
            }
            using (var brain = Brain(new Handler((_, _) => throw new HttpRequestException("transport " + Configuration.ApiKey))))
                Check((await brain.TestCloudConnection()).Contains("连接失败") && !brain.Status.Contains(Configuration.ApiKey), "Transport errors are classified and sanitized");
            using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Sse(); })) { Timeout = TimeSpan.FromMilliseconds(80) })
            using (var brain = new CompanionBrain(_ => throw new Exception(), TimeSpan.FromSeconds(1), cloudClient: client, cloudSettings: () => Configuration) { UseCloud = true })
                Check((await brain.TestCloudConnection()).Contains("超时"), "HTTP timeout is distinct from user cancellation");
            using (var brain = Brain(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Sse(); })))
            {
                var task = brain.Generate(false, false); brain.CancelGeneration(); await task;
                Check(brain.Status.Contains("取消") && !brain.Busy, "Cancellation releases the busy gate");
            }
            CompanionBrain.StreamingResponses = false;
            using (var brain = Brain(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}") }))))
                Check((await brain.TestCloudConnection()).Contains("成功"), "Non-streaming cloud completion is supported");
            CompanionBrain.StreamingResponses = true;
            await Snapshot();
            Check(Ui(Path.Combine(directory, "ui")) == 0, "Windows settings, quiet and request integration");
            Console.WriteLine($"Cloud and quiet verification: {checks} checks passed."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Environment.CurrentDirectory = originalDirectory; CompanionNative.TestPowerOverride = null; CompanionBrain.StreamingResponses = true; }
    }

    static void Config(string root)
    {
        var program = Path.Combine(root, "program"); var work = Path.Combine(root, "work"); var user = Path.Combine(root, "user", "cloud.json");
        Directory.CreateDirectory(program); Directory.CreateDirectory(work);
        CompanionCloud.Save(Path.Combine(program, "cloud.json"), Configuration.BaseUrl, "program", "program-key");
        CompanionCloud.Save(Path.Combine(work, "cloud.json"), Configuration.BaseUrl, "work", "work-key");
        CompanionCloud.Save(user, Configuration.BaseUrl, "user", "user-key");
        var env = new Dictionary<string, string?>();
        CompanionCloud.Editor Resolve() => CompanionCloud.Resolve(user, work, program, n => env.GetValueOrDefault(n));
        Check(Resolve().Model == "user" && Resolve().Sources.Contains("用户设置"), "User settings override working and program files");
        env["DEEPSEEK_MODEL"] = "environment";
        Check(Resolve().Model == "environment" && Resolve().ApiKey == "user-key", "Environment overrides by field, not whole document");
        CompanionCloud.Save(user, Configuration.BaseUrl, "user", "");
        Check(Resolve().ApiKey == "", "Empty user key clears credentials from lower priority files");
        env["DEEPSEEK_API_KEY"] = "env-key";
        Check(Resolve().ApiKey == "env-key" && Resolve().EnvironmentOverridesKey, "Environment key is visible as an override");
        env.Clear(); File.WriteAllText(user, "{broken");
        Check(Resolve().Model == "work" && Resolve().Warning.Contains("用户设置"), "Corrupt file is reported and lower priorities remain usable");
        File.Delete(Path.Combine(work, "cloud.json"));
        Check(Resolve().Model == "program", "Program defaults remain usable");
        CompanionCloud.Save(user, Configuration.BaseUrl, "locked", "fixture");
        var before = File.ReadAllText(user);
        using (var locked = File.Open(user, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false; try { CompanionCloud.Save(user, Configuration.BaseUrl, "replacement", "changed"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed, "Locked configuration reports a save failure");
        }
        Check(File.ReadAllText(user) == before && !Directory.GetFiles(Path.GetDirectoryName(user)!, "*.tmp").Any(), "Failed atomic save preserves the old configuration and cleans its temporary file");
        bool invalid = false; try { CompanionCloud.Save(user, "https://bad.invalid?secret=x", "model", "key"); } catch (IOException) { invalid = true; }
        Check(invalid && File.ReadAllText(user) == before, "Invalid URL is rejected before writing");
        var prefs = new CompanionPreferences();
        Check(prefs.MigrateMode(true) && prefs.CloudSelected && !prefs.MigrateMode(false), "Legacy cloud migration happens only once");
        prefs = new CompanionPreferences(); Check(prefs.MigrateMode(false) && !prefs.CloudSelected, "Legacy users without credentials keep local mode");
        var both = CompanionCloud.Resolve(Path.Combine(root, "absent.json"), program, program, _ => null);
        Check(both.Model == "program" && both.Sources.Contains("工作目录"), "Identical program and working paths resolve once with the highest priority source label");
    }

    static void Quiet()
    {
        var now = DateTimeOffset.Parse("2026-10-22T08:00:00Z"); var prefs = new CompanionPreferences { ModelMode = "Local" };
        var quiet = new CompanionQuietHours(prefs, () => now);
        quiet.Start(); Check(quiet.Active && prefs.QuietUntilUtc == now.AddHours(1), "Quiet stores an exact UTC deadline");
        prefs.Save();
        var saved = CompanionPreferences.Read();
        var restarted = new CompanionQuietHours(saved, () => now);
        Check(restarted.Active && restarted.MenuLabel.Contains("恢复"), "Quiet survives serialization and restart");
        now = now.AddHours(1);
        Check(restarted.Expire() && !restarted.Active && !restarted.Expire(), "Quiet expires once at its boundary without sleeping");
        quiet.Start(); quiet.Resume(); Check(!quiet.Active && prefs.QuietUntilUtc == null, "Manual resume clears the deadline");
    }

    static async Task Snapshot()
    {
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var selected = Configuration; string? sentModel = null;
        using var weather = new HttpClient(new Handler(async (_, token) => {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            return WeatherResponse();
        }));
        using var brain = Brain(new Handler(async (request, token) => {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); sentModel = body.RootElement.GetProperty("model").GetString(); return Sse();
        }), () => selected, weather);
        var generation = brain.GenerateWeather(); await entered.Task; selected = Configuration with { Model = "new-model" }; release.SetResult(); await generation;
        Check(sentModel == Configuration.Model, "Weather captures provider configuration before awaiting network input");
    }

    static int Ui(string directory)
    {
        int result = 1;
        var thread = new Thread(() => {
            MainWindow? pet = null;
            const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            void Pump(int ms = 50) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; App.Args = ["--test-mode"];
                Directory.CreateDirectory(directory); Environment.CurrentDirectory = directory;
                File.WriteAllText("preferences.json", "{\"ModelMode\":\"Local\",\"ModelEnabled\":false,\"PublicInfo\":false,\"ForegroundEnabled\":false,\"CheckUpdatesOnStartup\":false}");
                pet = new MainWindow(); pet.Show(); Pump(100);
                object Read(string n) => pet.GetType().GetField(n, access)!.GetValue(pet)!;
                void Set(string n, object v) => pet.GetType().GetField(n, access)!.SetValue(pet, v);
                object? Invoke(string n, params object[] args) => pet.GetType().GetMethod(n, access)!.Invoke(pet, args);
                ((DispatcherTimer)Read("companionTimer")).Stop();
                var prefs = (CompanionPreferences)Read("preferences"); var quiet = (CompanionQuietHours)Read("quietHours");
                Invoke("ToggleQuietHours"); Check(quiet.Active, "Quiet menu starts the deadline");
                Invoke("ShowCompanionNotice", "manual", false); Invoke("ToggleQuietHours"); Invoke("ToggleQuietHours");
                Check(pet.CompanionBubble.Visibility == Visibility.Visible && pet.CompanionBubbleText.Text == "manual", "Quiet preserves manually requested notices");
                Set("automaticRequestPending", true); Invoke("ShowCompanionBubble", "automatic"); Set("automaticRequestPending", false);
                Invoke("ToggleQuietHours"); Invoke("ToggleQuietHours");
                Check(pet.CompanionBubble.Visibility != Visibility.Visible, "Quiet removes an automatic bubble");
                bool hasMcp = pet.GetType().GetMethod("ShowMcpSpeech", access) != null;
                if (hasMcp)
                {
                    Invoke("ShowMcpSpeech", "agent text"); Invoke("ToggleQuietHours"); Invoke("ToggleQuietHours");
                    Check(pet.CompanionBubble.Visibility == Visibility.Visible, "Quiet preserves immediate Agent speech");
                    Set("mcpNextSpeech", "scheduled agent"); Check((bool)Invoke("ConsumeMcpSpeech")!, "Queued Agent speech remains consumable during quiet");
                }
                else Check(!typeof(MainWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name == "BigBlueFish.Control"), "Cloud and quiet are available without a Control reference in Safe");
                Invoke("ToggleQuietHours");
                Check((DateTime)Read("nextSpeech") > DateTime.UtcNow.AddMinutes(9), "Resuming schedules a fresh interval without catch-up");
                var waiting = new TaskCompletionSource();
                ((CompanionBrain)Read("brain")).Dispose();
                var fake = Brain(new Handler(async (_, token) => { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Sse(); }));
                Set("brain", fake); prefs.ModelEnabled = true; prefs.ModelMode = "Cloud";
                prefs.HideOnFullscreen = false; Set("fullscreenHidden", false); pet.Show();
                Set("companionReady", true);
                var automatic = (Task)Invoke("SpeechCountdownZeroAsync", true)!;
                var deadline = Stopwatch.StartNew(); while (!waiting.Task.IsCompleted && deadline.ElapsedMilliseconds < 2000) Pump();
                Check(waiting.Task.IsCompleted, $"Automatic request reaches the injected transport (available={fake.ModelAvailable}, quiet={quiet.Active}, pending={Read("speechRequestPending")}, hidden={Read("hiddenByUser")}, fullscreen={Read("fullscreenHidden")}, status={fake.Status})");
                var version = (long)Read("speechRequestVersion"); Invoke("ToggleQuietHours");
                while (!automatic.IsCompleted && deadline.ElapsedMilliseconds < 3000) Pump();
                Invoke("ShowCompanionPartial", "late automatic reply", (long?)version!);
                Check(automatic.IsCompleted && pet.CompanionBubbleText.Text != "late automatic reply" && ((Border)Read("loadingIndicator")).Visibility != Visibility.Visible, "Quiet cancels automatic work and rejects its stale callback");
                fake.Dispose();
                int manualCalls = 0;
                using var weatherClient = new HttpClient(new Handler((_, _) => Task.FromResult(WeatherResponse())));
                var success = Brain(new Handler((_, _) => { manualCalls++; return Task.FromResult(Sse()); }), weather: weatherClient); Set("brain", success);
                var suppressed = (Task)Invoke("SpeechCountdownZeroAsync", true)!;
                Check(suppressed.IsCompleted && manualCalls == 0, "Quiet blocks automatic generation before any HTTP request");
                var manual = (Task)Invoke("SpeakCompanionTest")!;
                var manualDeadline = Stopwatch.StartNew(); while (!manual.IsCompleted && manualDeadline.ElapsedMilliseconds < 2000) Pump();
                Check(manual.IsCompleted && pet.CompanionBubbleText.Text == "主人好～" && quiet.Active, "Manual speech test still works while quiet");
                var manualWeather = (Task)Invoke("SpeakCompanionWeather")!;
                while (!manualWeather.IsCompleted && manualDeadline.ElapsedMilliseconds < 3000) Pump();
                Check(manualWeather.IsCompleted && manualCalls == 2 && quiet.Active && pet.CompanionBubbleText.Text == "主人好～", "Manual weather also works while quiet using fixture weather");
                success.Dispose();
                var started = new TaskCompletionSource();
                var switching = Brain(new Handler(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Sse(); })); Set("brain", switching);
                var beforeSwitch = (Task)Invoke("SpeakCompanionTest")!;
                while (!started.Task.IsCompleted && manualDeadline.ElapsedMilliseconds < 3000) Pump();
                Check(started.Task.IsCompleted, "Manual request starts before switching mode");
                var staleVersion = (long)Read("speechRequestVersion"); prefs.ModelMode = "Local"; Invoke("ApplyModelConfiguration");
                while (!beforeSwitch.IsCompleted && manualDeadline.ElapsedMilliseconds < 4000) Pump();
                Invoke("ShowCompanionPartial", "stale mode reply", (long?)staleVersion!);
                Check(beforeSwitch.IsCompleted && !switching.UseCloud && pet.CompanionBubbleText.Text != "stale mode reply", "Mode switch cancels pending generation and invalidates its callbacks");
                switching.Dispose();
                var testStarted = new TaskCompletionSource();
                var testingBrain = Brain(new Handler(async (_, token) => { testStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Sse(); }));
                Set("brain", testingBrain); prefs.ModelMode = "Cloud";
                if (quiet.Active) Invoke("ToggleQuietHours");
                var connectionTest = testingBrain.TestCloudConnection();
                var queuedAutomatic = (Task)Invoke("SpeechCountdownZeroAsync", true)!;
                Invoke("ToggleQuietHours"); Pump(100);
                Check(testStarted.Task.IsCompleted && !connectionTest.IsCompleted && testingBrain.Busy, "Quiet does not cancel a manual connection test when automatic speech is waiting");
                testingBrain.CancelGeneration();
                var cleanupDeadline = Stopwatch.StartNew(); while ((!connectionTest.IsCompleted || !queuedAutomatic.IsCompleted) && cleanupDeadline.ElapsedMilliseconds < 2000) Pump();
                Check(connectionTest.IsCompleted && queuedAutomatic.IsCompleted, "Cancelled automatic waiter releases cleanly after manual work");
                if (hasMcp)
                {
                    var mcpSettings = (Dictionary<string, object?>)Invoke("CurrentMcpSettings")!;
                    Check(!mcpSettings.ContainsKey("ModelMode") && !mcpSettings.ContainsKey("QuietUntilUtc") && !mcpSettings.ContainsKey("StopCloudOnBattery"), "New preferences do not expand the MCP settings interface");
                }
                Invoke("OpenCompanionSettings"); Pump(100);
                var settings = (Window)Read("companionSettings");
                IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Children<T>(child)) yield return nested; } }
                Children<Button>(settings).First(b => b.Content?.ToString() == "模型与供电").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(Children<Button>(settings).Any(b => b.Content?.ToString() == "测试已保存的云端连接"), "Cloud connection test is present in settings");
                var image = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(settings);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(directory, "settings.png"))) encoder.Save(file);
                Check(settings.IsVisible, "Settings render with explicit mode and cloud controls");
                var view = Children<ScrollViewer>(settings).First(v => v.Visibility == Visibility.Visible);
                view.ScrollToEnd(); Pump();
                var lower = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32); lower.Render(settings);
                var lowerEncoder = new PngBitmapEncoder(); lowerEncoder.Frames.Add(BitmapFrame.Create(lower)); using (var file = File.Create(Path.Combine(directory, "settings-cloud.png"))) lowerEncoder.Save(file);
                settings.Close();
                prefs.QuietUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1); Set("nextSpeech", DateTime.MinValue);
                Invoke("CompanionTick", null!, EventArgs.Empty); Pump();
                Check(prefs.QuietUntilUtc == null && (DateTime)Read("nextSpeech") > DateTime.UtcNow.AddMinutes(9), "Expiry clears the persisted deadline and schedules a fresh interval");
                result = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally { if (pet != null) { pet.GetType().GetField("exitReady", access)!.SetValue(pet, true); pet.Close(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
}
