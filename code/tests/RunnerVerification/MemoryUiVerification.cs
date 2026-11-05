using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BigBlueFish.Memory;
using BigBlueFish.Windows;

internal static class MemoryUiVerification
{
    public static int Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        var result = 1;
        var thread = new Thread(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MainWindow? pet = null;
            var previousMemory = Environment.GetEnvironmentVariable("BIGBLUEFISH_MEMORY");
            var previousDirectory = Environment.CurrentDirectory;
            var checks = 0;
            void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
            void Pump()
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            }
            IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T value) yield return value;
                    foreach (var nested in Children<T>(child)) yield return nested;
                }
            }
            void Save(Window window, string name)
            {
                window.UpdateLayout(); Pump();
                FrameworkElement content = window;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
            }
            try
            {
                _ = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                App.Args = ["--test-mode"];
                Directory.CreateDirectory(directory);
                Environment.CurrentDirectory = directory;
                Environment.SetEnvironmentVariable("BIGBLUEFISH_MEMORY", Path.Combine(directory, "isolated-memory-" + Guid.NewGuid().ToString("N")));
                File.WriteAllText("preferences.json", """{"ModelEnabled":false,"PublicInfo":false,"ForegroundEnabled":false,"CheckUpdatesOnStartup":false,"MemoryEnabled":true,"SyncEnabled":false,"McpEnabled":false}""");
                pet = new MainWindow(); pet.Show(); Pump();
                object Read(string name) => pet.GetType().GetField(name, flags)!.GetValue(pet)!;
                ((DispatcherTimer)Read("companionTimer")).Stop();
                pet.GetType().GetMethod("OpenCompanionSettings", flags)!.Invoke(pet, null); Pump();
                var settings = (Window)Read("companionSettings");
                Check(settings.WindowStyle == WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(settings)?.CaptionHeight == 48,
                    "Settings uses custom chrome with native caption hit testing");
                var full = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen()) context.DrawRectangle(new VisualBrush(settings), null, new Rect(0, 0, settings.ActualWidth, settings.ActualHeight));
                full.Render(visual); var fullEncoder = new PngBitmapEncoder(); fullEncoder.Frames.Add(BitmapFrame.Create(full));
                using (var file = File.Create(Path.Combine(directory, "settings-appearance.png"))) fullEncoder.Save(file);
                void Caption(string name)
                {
                    var caption = (System.Windows.Controls.Button)settings.Template.FindName(name, settings);
                    ((System.Windows.Input.RoutedCommand)caption.Command).Execute(null, caption); Pump();
                }
                Caption("Maximize"); Check(settings.WindowState == WindowState.Maximized, "Custom maximize button works");
                Caption("Maximize"); Check(settings.WindowState == WindowState.Normal, "Custom maximize button becomes restore");
                Caption("Minimize"); Check(settings.WindowState == WindowState.Minimized, "Custom minimize button works");
                SystemCommands.RestoreWindow(settings); Pump();
                Check(settings.WindowState == WindowState.Normal && !settings.Topmost, "Restored settings stays a normal non-topmost window");
                Check(Children<CompanionSettingsBackdrop>(settings).Single().IsHitTestVisible == false, "Decorative backdrop never intercepts settings input");
                var agentNavigation = Children<System.Windows.Controls.Button>(settings).FirstOrDefault(b => b.Content?.ToString() == "Agent 与 MCP");
                if (agentNavigation != null)
                {
                    agentNavigation.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                    var mcpToggle = Children<System.Windows.Controls.CheckBox>(settings).Single(c => c.Content?.ToString() == "开启 MCP 接口");
                    Check(mcpToggle.FontFamily.Source.Contains("乐米"), "MCP labels inherit the cute font rather than a system-face override");
                    Save(settings, "settings-agent.png");
                }
                Children<System.Windows.Controls.Button>(settings).First(b => b.Content?.ToString() == "鱼鱼的记忆")
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var view = Children<ScrollViewer>(settings).First(v => v.Visibility == Visibility.Visible && v.Content is StackPanel);
                var panel = (StackPanel)view.Content;
                var buttons = Children<System.Windows.Controls.Button>(panel).ToArray();
                System.Windows.Controls.Button Button(string id) => Children<System.Windows.Controls.Button>(panel).First(b => AutomationProperties.GetAutomationId(b) == id);
                Save(settings, "memory-empty.png");
                Button("memory-nav-1").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var transferDetails = Children<Expander>(panel).First(expander => expander.Header?.ToString() == "连接另一台电脑");
                transferDetails.IsExpanded = true; Pump();
                foreach (var id in new[] { "memory-create-archive", "memory-publish-transfer", "memory-redeem-transfer", "memory-keep-local", "memory-use-cloud" })
                    Check(Children<System.Windows.Controls.Button>(panel).Any(b => AutomationProperties.GetAutomationId(b) == id), "Memory control available: " + id);
                transferDetails.IsExpanded = false;
                Button("memory-nav-0").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var editor = Children<System.Windows.Controls.TextBox>(panel).Single(box => box.AcceptsReturn);
                editor.Text = "界面验证：喜欢喝茶";
                Button("memory-save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var memory = (CompanionMemory)Read("companionMemory");
                var item = memory.List().Single();
                Check(item.Content == "界面验证：喜欢喝茶" && memory.Database.Outbox().Count == 0, "UI saves only to the isolated local database while unpaired");
                memory.Database.SyncArmed = true;
                memory.Update(item.Id, "我喜欢喝茶。", false, false);
                memory.Database.ApplyRemote(item with { Content = "我喜欢喝清淡的红茶。", Revision = 2 });
                ((Action)Read("refreshMemoryList"))();
                var list = Children<System.Windows.Controls.ListBox>(panel).Single(); list.SelectedIndex = 0; Pump();
                Save(settings, "memory-conflict.png");
                Button("memory-use-cloud")
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                Check(memory.Conflicts().Count == 0 && memory.List().Single().Content == "我喜欢喝清淡的红茶。", "UI conflict choice updates the retained text");
                Check(editor.Text == "我喜欢喝清淡的红茶。", "Editor refreshes after conflict resolution instead of retaining stale text");
                memory.Remember("称呼我小黎就好。", true, false);
                memory.Remember("最近在做蓝色大肥鱼桌宠。", false, false);
                ((Action)Read("refreshMemoryList"))(); Pump();
                view.ScrollToTop(); Pump(); Save(settings, "memory-top.png");
                Button("memory-nav-1").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var create = Button("memory-create-archive");
                create.BringIntoView(); Pump(); Save(settings, "memory-pairing.png");
                var y = create.TranslatePoint(new System.Windows.Point(0, 0), view).Y;
                Check(y >= 0 && y + create.ActualHeight <= view.ActualHeight, "Archive initialization control is reachable on the sync tab");
                Button("memory-nav-2").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                view.ScrollToTop(); Pump(); Save(settings, "memory-bottom.png");
                Check(!list.IsVisible && Button("memory-export").IsVisible, "Recovery controls live on their own page without cluttering daily memory editing");
                settings.Width = 700; settings.Height = 570; Pump();
                Button("memory-nav-0").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                Button("memory-edit").BringIntoView(); Pump(); Save(settings, "memory-compact.png");
                Check(editor.ActualWidth > 200 && editor.ActualWidth < panel.ActualWidth, "Compact window retains a usable editor without horizontal overflow");
                Check(!Children<TextBlock>(settings).Any(t => t.Text.Contains("维护版")), "Settings version label has no maintainer branding");
                pet.GetType().GetField("fishChatHistory", flags)!.SetValue(pet, new List<(string Role, string Content)>
                {
                    ("user", "今天把设置改成浅蓝色啦。"),
                    ("assistant", "唔，像把一小片天空搬进桌面惹～"),
                    ("user", "鱼鱼，陪我聊一会儿。")
                });
                pet.GetType().GetMethod("OpenFishChat", flags)!.Invoke(pet, null); Pump();
                var chat = (Window)Read("fishChatWindow");
                Check(chat.WindowStyle == WindowStyle.None && chat.FontFamily.Source.Contains("乐米"), "Chat shares the cute font and custom frame");
                Save(chat, "fish-chat.png");
                var chatInput = Children<System.Windows.Controls.TextBox>(chat).Single(t => AutomationProperties.GetAutomationId(t) == "fish-chat-input");
                var chatSend = Children<System.Windows.Controls.Button>(chat).Single(b => AutomationProperties.GetAutomationId(b) == "fish-chat-send");
                Check(!chatSend.IsEnabled, "Empty chat cannot submit");
                var chatStatus = Children<TextBlock>(chat).Single(t => AutomationProperties.GetAutomationId(t) == "fish-chat-status");
                Check(chatStatus.Visibility == Visibility.Collapsed && chatInput.ToolTip == null, "Idle chat hides model status and duplicate shortcut tooltip");
                var chatHint = Children<TextBlock>(chat).Single(t => t.Text == "想和鱼鱼说点什么？");
                System.Windows.Input.Keyboard.ClearFocus(); Pump();
                var hintPosition = chatHint.TranslatePoint(new System.Windows.Point(0, 0), chatInput);
                Check(chatHint.IsVisible && Math.Abs(hintPosition.Y + chatHint.ActualHeight / 2 - chatInput.ActualHeight / 2) < 2,
                    "Empty placeholder is vertically centered in the input");
                Save(chat, "chat-input-empty.png");
                chatInput.Focus(); Pump();
                Check(!chatHint.IsVisible, "Focusing the input hides the placeholder");
                chatInput.Text = "你好"; Pump();
                var caret = chatInput.GetRectFromCharacterIndex(0);
                Check(!caret.IsEmpty && Math.Abs(caret.Top + caret.Height / 2 - chatInput.ActualHeight / 2) < 3,
                    "Typed text and caret are vertically centered");
                chatInput.Text = "记住界面验证：喜欢浅蓝色";
                chatSend.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                Check(memory.List().Any(item => item.Content == "界面验证：喜欢浅蓝色") && Children<TextBlock>(chat).Any(t => t.Text == "记住了。"),
                    "Styled chat still saves explicit memory and paints the reply without a model call");
                chat.Close(); settings.Close();
                Console.WriteLine("MEMORY-UI-OK " + checks);
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (pet != null) { pet.GetType().GetField("exitReady", flags)!.SetValue(pet, true); pet.Close(); }
                Environment.SetEnvironmentVariable("BIGBLUEFISH_MEMORY", previousMemory);
                Environment.CurrentDirectory = previousDirectory;
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
}
