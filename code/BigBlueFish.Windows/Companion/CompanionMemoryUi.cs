using BigBlueFish.Memory;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private CompanionMemory? companionMemory;
    private CancellationTokenSource? memorySyncLoop;
    private Action? refreshMemoryList;
    private Window? fishChatWindow;
    private List<(string Role, string Content)>? fishChatHistory;
    private readonly Dictionary<int, List<SearchSource>> fishChatSources = new();

    private CompanionMemory EnsureCompanionMemory()
    {
        if (companionMemory == null)
        {
            companionMemory = CompanionMemory.Open();
            memorySyncLoop ??= new CancellationTokenSource();
            if (memorySyncLoop.IsCancellationRequested) memorySyncLoop = new CancellationTokenSource();
            _ = RunMemorySync(memorySyncLoop.Token);
        }
        companionMemory.Enabled = preferences.MemoryEnabled;
        companionMemory.SyncEnabled = preferences.SyncEnabled;
        companionMemory.ServerUrl = preferences.SyncServerUrl;
        return companionMemory;
    }

    private async Task RunMemorySync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60 + Random.Shared.Next(0, 20)), token);
                if (companionMemory is { Enabled: true, SyncEnabled: true })
                    try { await companionMemory.SyncNow(); } catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OpenFishChat()
    {
        if (fishChatWindow != null) { if (fishChatWindow.WindowState == WindowState.Minimized) fishChatWindow.WindowState = WindowState.Normal; fishChatWindow.Activate(); return; }
        var messages = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var transcript = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brushes.Transparent };
        var input = new TextBox { AcceptsReturn = true, MinHeight = 64, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var state = Note("");
        state.FontSize = 16;
        state.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(state, "fish-chat-status");
        void ChatStatus(string message)
        {
            state.Text = message;
            state.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        input.MaxLength = 4000;
        AutomationProperties.SetAutomationId(input, "fish-chat-input");
        var history = fishChatHistory ??= new List<(string Role, string Content)>();
        var chatLifetime = new CancellationTokenSource();
        try
        {
            if (preferences.MemoryEnabled && history.Count == 0)
                foreach (var message in EnsureCompanionMemory().Recent()) history.Add((message.Role, message.Content));
        }
        catch (Exception error) { ChatStatus(error.Message); }
        void Paint()
        {
            messages.Children.Clear();
            if (history.Count == 0)
            {
                var welcome = new StackPanel { Margin = new Thickness(16, 24, 16, 24) };
                welcome.Children.Add(new TextBlock { Text = "今天过得怎么样？", FontSize = 25, Foreground = new SolidColorBrush(Color.FromRgb(78, 103, 151)), TextAlignment = TextAlignment.Center });
                welcome.Children.Add(new TextBlock { Text = "想说的话，都可以慢慢告诉鱼鱼。", FontSize = 17, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 9, 0, 0) });
                messages.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), CornerRadius = new CornerRadius(18), Child = welcome });
            }
            for (int index = 0; index < history.Count; index++)
            {
                var item = history[index];
                bool mine = item.Role == "user";
                var row = new StackPanel { HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    MaxWidth = 300, Margin = new Thickness(mine ? 30 : 0, 5, mine ? 0 : 30, 7) };
                row.Children.Add(new TextBlock { Text = mine ? "我" : "鱼鱼", FontSize = 16,
                    Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(5, 0, 5, 4),
                    HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left });
                row.Children.Add(new Border { CornerRadius = mine ? new CornerRadius(17, 17, 5, 17) : new CornerRadius(5, 17, 17, 17), Padding = new Thickness(14, 11, 14, 11),
                    Background = new SolidColorBrush(mine ? Color.FromRgb(220, 233, 255) : Color.FromArgb(230, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(213, 226, 247)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = item.Content, TextWrapping = TextWrapping.Wrap, FontSize = 20,
                        Foreground = new SolidColorBrush(Color.FromRgb(65, 85, 124)) } });
                if (fishChatSources.TryGetValue(index, out var sources))
                    foreach (var source in sources)
                    {
                        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(source.Title)) { NavigateUri = new Uri(source.Url) };
                        link.RequestNavigate += (_, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { ChatStatus("无法打开来源链接。"); } e.Handled = true; };
                        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 0, 0), FontSize = 14 };
                        line.Inlines.Add(link); row.Children.Add(line);
                    }
                messages.Children.Add(row);
            }
            transcript.ScrollToEnd();
            transcript.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)transcript.ScrollToEnd);
        }
        Paint();
        var send = new Button { Content = "发送", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left };
        bool chatSending = false;
        send.IsEnabled = false;
        AutomationProperties.SetAutomationId(send, "fish-chat-send");
        send.Click += async (_, _) =>
        {
            var text = input.Text.Trim();
            if (text.Length == 0 || brain.Busy) { if (brain.Busy) ChatStatus("鱼鱼正在说话，等这一句结束。"); return; }
            if (text.StartsWith("记住", StringComparison.Ordinal) || text.StartsWith("请记住", StringComparison.Ordinal))
            {
                if (!preferences.MemoryEnabled) { ChatStatus("记忆还关着。先在“鱼鱼的记忆”里打开。"); return; }
                var fact = text.StartsWith("请记住", StringComparison.Ordinal) ? text["请记住".Length..] : text["记住".Length..];
                fact = fact.TrimStart('，', ',', '：', ':', ' ');
                if (fact.Length == 0) { ChatStatus("记住后面还要有内容。"); return; }
                try
                {
                    EnsureCompanionMemory().Remember(fact, false, false);
                    history.Add(("user", text));
                    history.Add(("assistant", "记住了。"));
                    input.Text = "";
                    Paint();
                    ChatStatus("");
                    refreshMemoryList?.Invoke();
                }
                catch (Exception error) { ChatStatus(error.Message); }
                return;
            }
            if (PuppetActive) { ChatStatus("当前由 Agent 控制鱼鱼，请先退出接管模式再聊天。"); return; }
            if (!preferences.ModelEnabled || !brain.ModelAvailable) { ChatStatus("暂时无法聊天，请到设置查看。"); return; }
            if (!brain.CanRunOnCurrentPower) { ChatStatus("当前暂停了对话，请到设置查看。"); return; }
            input.Text = "";
            history.Add(("user", text));
            Paint();
            chatSending = true;
            send.IsEnabled = false;
            send.Content = "等鱼鱼一下…";
            ChatStatus("鱼鱼正在想怎么回复你…");
            try
            {
                var prior = history.Take(history.Count - 1).ToArray();
                string searchNotice = preferences.ChatWebSearchEnabled && !brain.SearchAvailable() ? "联网搜索仅支持官方 DeepSeek 云端配置，本次未联网。" : "";
                var reply = await brain.ChatReply(text, prior, chatLifetime.Token, preferences.ChatWebSearchEnabled, notice => { ChatStatus(notice); if (!notice.Contains("正在搜索")) searchNotice = notice; });
                chatLifetime.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(reply)) { ChatStatus("这次没能收到回复，请稍后再试。"); return; }
                history.Add(("assistant", reply));
                if (brain.LastSearchSources.Count > 0) fishChatSources[history.Count - 1] = brain.LastSearchSources.ToList();
                if (preferences.MemoryEnabled)
                {
                    var memory = EnsureCompanionMemory();
                    memory.AddChat("user", text);
                    memory.AddChat("assistant", reply);
                }
                Paint();
                ChatStatus(searchNotice);
            }
            catch (OperationCanceledException) { ChatStatus("对话已取消"); }
            catch (Exception error) { ChatStatus(error.Message); }
            finally { chatSending = false; send.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); send.Content = "发送"; }
        };
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition());
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(18, 14, 18, 12) };
        heading.Children.Add(new TextBlock { Text = "陪鱼鱼聊聊", FontSize = 28, FontWeight = FontWeights.Normal,
            Foreground = System.Windows.Media.Brushes.MidnightBlue });
        heading.Children.Add(Note("属于你和鱼鱼的小小聊天角落"));
        stack.Children.Add(heading);
        Grid.SetRow(transcript, 1); stack.Children.Add(transcript);
        var composer = new StackPanel { Margin = new Thickness(16, 6, 16, 12) };
        input.MaxHeight = 120;
        input.FontSize = 20;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        input.Padding = new Thickness(10);
        input.ToolTip = null;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        send.HorizontalAlignment = HorizontalAlignment.Right;
        send.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 116, 225));
        send.Foreground = System.Windows.Media.Brushes.White;
        send.BorderThickness = new Thickness(0);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
            {
                e.Handled = true;
                if (send.IsEnabled) send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        var editor = new Grid(); editor.Children.Add(input);
        var placeholder = new TextBlock { Text = "想和鱼鱼说点什么？", FontSize = input.FontSize, Foreground = Brushes.LightSlateGray,
            Margin = new Thickness(11, 8, 11, 8), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        editor.Children.Add(placeholder);
        void RefreshPlaceholder() => placeholder.Visibility = input.Text.Length == 0 && !input.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        input.GotKeyboardFocus += (_, _) => RefreshPlaceholder();
        input.LostKeyboardFocus += (_, _) => RefreshPlaceholder();
        input.TextChanged += (_, _) =>
        {
            RefreshPlaceholder();
            send.IsEnabled = !chatSending && !string.IsNullOrWhiteSpace(input.Text);
        };
        var actions = new Grid { Margin = new Thickness(0, 2, 0, 3) };
        actions.Children.Add(new TextBlock { Text = "Enter 发送 · Shift+Enter 换行", FontSize = 15, Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 88, 0) });
        actions.Children.Add(send);
        var webToggle = new CheckBox { Content = "联网搜索（可能产生费用）", IsChecked = preferences.ChatWebSearchEnabled, FontSize = 15, Margin = new Thickness(0, 4, 0, 2) };
        AutomationProperties.SetAutomationId(webToggle, "fish-chat-web-search");
        webToggle.Click += (_, _) => { preferences.ChatWebSearchEnabled = webToggle.IsChecked == true; SaveCompanion(); ChatStatus(preferences.ChatWebSearchEnabled && !brain.SearchAvailable() ? "联网搜索仅支持官方 DeepSeek 云端配置。" : ""); };
        composer.Children.Add(webToggle); composer.Children.Add(editor); composer.Children.Add(actions); composer.Children.Add(state);
        if (preferences.ChatWebSearchEnabled && !brain.SearchAvailable()) ChatStatus("联网搜索仅支持官方 DeepSeek 云端配置。");
        var composerFrame = new Border { Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(221, 231, 247)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Margin = new Thickness(12, 0, 12, 12), Child = composer };
        Grid.SetRow(composerFrame, 2); stack.Children.Add(composerFrame);
        fishChatWindow = new Window
        {
            Title = "与鱼鱼聊天",
            Width = 440,
            Height = 640,
            MinWidth = 350,
            MinHeight = 420,
            Owner = this,
            FontFamily = CompanionSettingsTheme.CuteFont,
            FontSize = 20,
            ShowInTaskbar = false,
            Topmost = true,
            Background = System.Windows.Media.Brushes.White,
            Content = CompanionSettingsTheme.Scene(stack),
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        CompanionSettingsTheme.Apply(fishChatWindow, "鱼鱼的聊天角落");
        send.Style = (Style)fishChatWindow.FindResource("FishPrimaryButtonStyle");
        fishChatWindow.Closed += (_, _) => { fishChatWindow = null; chatLifetime.Cancel(); };
        var area = SystemParameters.WorkArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)((Left + Width / 2) * dpi.DpiScaleX), (int)((Top + Height / 2) * dpi.DpiScaleY)));
        area = new Rect(screen.WorkingArea.X / dpi.DpiScaleX, screen.WorkingArea.Y / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX, screen.WorkingArea.Height / dpi.DpiScaleY);
        double beside = Left + Width + 10;
        if (beside + fishChatWindow.Width > area.Right) beside = Left - fishChatWindow.Width - 10;
        fishChatWindow.Left = Math.Clamp(beside, area.Left, Math.Max(area.Left, area.Right - fishChatWindow.Width));
        fishChatWindow.Top = Math.Clamp(Top + Height / 2 - fishChatWindow.Height / 2, area.Top, Math.Max(area.Top, area.Bottom - fishChatWindow.Height));
        fishChatWindow.Show();
        input.Focus();
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = System.Windows.Media.Brushes.SlateGray,
        Margin = new Thickness(0, 4, 0, 4)
    };

    private static void MemoryButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(error.Message, "鱼鱼的记忆"); }
        };
        panel.Children.Add(button);
    }
}
