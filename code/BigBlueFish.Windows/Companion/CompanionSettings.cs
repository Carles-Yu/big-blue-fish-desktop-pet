using BigBlueFish.Deployment;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;
public partial class MainWindow
{
    private Action? refreshCompanionSettings;
    private Action? disposeCompanionSettingsContent;
    private void OpenCompanionSettings()
    {
        if (exitAnimation || companionClosed || exitReady) return;
        if (companionSettings != null)
        {
            ActivateCompanionSettings(companionSettings);
            return;
        }

        CancelMovement();
        var w = new Window
        {
            Title = "大肥鱼设置",
            Width = Math.Min(860, SystemParameters.WorkArea.Width - 32),
            Height = Math.Min(690, SystemParameters.WorkArea.Height - 32),
            MinWidth = Math.Min(680, SystemParameters.WorkArea.Width - 32),
            MinHeight = Math.Min(450, SystemParameters.WorkArea.Height - 32),
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 252)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            ShowInTaskbar = true,
            ShowActivated = true,
            Topmost = false
        };
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/BigBlueFish;component/Companion/CompanionSettingsStyles.xaml", UriKind.Relative) });
        companionSettings = w;
        BuildCompanionSettingsContent(w);
        w.Closed += (_, _) =>
        {
            disposeCompanionSettingsContent?.Invoke();
            disposeCompanionSettingsContent = refreshCompanionSettings = null;
            companionSettings = null;
            SaveCompanion();
        };
        ActivateCompanionSettings(w);
    }

    private void BuildCompanionSettingsContent(Window w, int selectedPage = 0, double[]? scrollOffsets = null)
    {
        disposeCompanionSettingsContent?.Invoke();
        var shell = new Grid { Background = w.Background };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        w.Content = shell;
        var sidebar = new StackPanel { Margin = new Thickness(18, 24, 18, 18) };
        var sidebarFrame = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
        shell.Children.Add(sidebarFrame);
        sidebar.Children.Add(new TextBlock { Text = "蓝色大肥鱼", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(5, 0, 0, 8) });
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
#if COMPANION_SAFE
        version += " · 无MCP版";
#endif
        sidebar.Children.Add(new TextBlock { Text = version + " · 陪伴设置", Foreground = Brushes.SlateGray, Margin = new Thickness(5, 0, 0, 28) });
#if COMPANION_SAFE
        var titles = new[] { "陪伴与外观", "说话与天气", "模型与供电" };
#else
        var titles = new[] { "陪伴与外观", "说话与天气", "模型与供电", "Agent 与 MCP" };
#endif
        var pages = new StackPanel[titles.Length];
        var views = new ScrollViewer[titles.Length];
        var navigation = new Button[titles.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = new StackPanel { Margin = new Thickness(26, 24, 26, 24) };
            pages[i].Children.Add(new TextBlock { Text = titles[i], FontSize = 25, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(42, 56, 83)), Margin = new Thickness(0, 0, 0, 20) });
            views[i] = new ScrollViewer { Content = pages[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = i == selectedPage ? Visibility.Visible : Visibility.Collapsed };
            Grid.SetColumn(views[i], 1); shell.Children.Add(views[i]);
            int selected = i;
            var button = navigation[i] = new Button { Content = titles[i], HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(0), Background = i == selectedPage ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent };
            button.Click += (_, _) =>
            {
                selectedPage = selected;
                for (int n = 0; n < views.Length; n++)
                { views[n].Visibility = n == selected ? Visibility.Visible : Visibility.Collapsed; navigation[n].Background = n == selected ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent; }
            };
            sidebar.Children.Add(button);
        }
        void Card(StackPanel panel)
        {
            var children = panel.Children.Cast<UIElement>().Skip(1).ToArray();
            foreach (var child in children) panel.Children.Remove(child);
            var inner = new StackPanel { Margin = new Thickness(20) };
            foreach (var child in children) inner.Children.Add(child);
            panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = inner });
        }
        var p = pages[2];
        void Toggle(string label, bool value, Action<bool> change)
        {
            var c = new CheckBox
            {
                Content = label,
                IsChecked = value,
                Margin = new Thickness(0, 7, 0, 7)
            };
            c.Click += (_, _) =>
            {
                change(c.IsChecked == true);
                SaveCompanion();
            };
            p.Children.Add(c);
        }

        Toggle("启用 AI 陪伴", preferences.ModelEnabled, v =>
        {
            preferences.ModelEnabled = v;
            if (!v)
                ApplyModelConfiguration();
            else
            {
                nextWarmup = DateTime.MinValue;
                nextSpeech = DateTime.UtcNow.AddSeconds(20);
            }
        });
        p.Children.Add(new TextBlock
        {
            Text = "请明确选择本地或云端模型；保存 Key 不会切换模式。",
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var mode = new ComboBox { ItemsSource = new[] { "本地模型", "云端模型" }, SelectedIndex = preferences.CloudSelected ? 1 : 0, Margin = new Thickness(0, 6, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(mode, "模型模式");
        p.Children.Add(mode);
        var screenNotice = new TextBlock { Text = CompanionCloud.ScreenNotice, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Visibility = preferences.CloudSelected ? Visibility.Visible : Visibility.Collapsed };
        p.Children.Add(screenNotice);
        mode.SelectionChanged += (_, _) => {
            preferences.ModelMode = mode.SelectedIndex == 1 ? "Cloud" : "Local";
            screenNotice.Visibility = preferences.CloudSelected ? Visibility.Visible : Visibility.Collapsed;
            ApplyModelConfiguration();
        };
        Toggle("电池供电时暂停云端调用", preferences.StopCloudOnBattery, v => { preferences.StopCloudOnBattery = v; ApplyCompanionPowerPolicy(); });
        Toggle("拔电时暂停并卸载本地模型", preferences.StopModelOnBattery, v =>
        {
            preferences.StopModelOnBattery = v;
            ApplyCompanionPowerPolicy();
        });
        Toggle("使用显卡加速（关闭则使用 CPU）", preferences.GpuEnabled, v =>
        {
            preferences.GpuEnabled = v;
            brain.Stop();
            brain.UseGpu = v;
            nextWarmup = DateTime.MinValue;
        });
        p = pages[0];
        Toggle("全屏时自动隐藏桌宠", preferences.HideOnFullscreen, v =>
        {
            preferences.HideOnFullscreen = v;
            if (!v && fullscreenHidden)
            {
                fullscreenHidden = false;
                if (!hiddenByUser) { Show(); ClampCompanion(); }
            }
        });
        Toggle("开机自动启动", StartupEnabled(), SetCompanionStartup);
        Toggle("启动时检查更新", preferences.CheckUpdatesOnStartup, v => preferences.CheckUpdatesOnStartup = v);
        p = pages[1];
        Toggle("自动获取天气和科技新闻", preferences.PublicInfo, v => preferences.PublicInfo = v);
        Toggle("感知当前应用（云端模式会发送 OCR 文字）", preferences.ForegroundEnabled, v => { preferences.ForegroundEnabled = v; ApplyModelConfiguration(); });
        AddCompanionRegionSelectors(p);
        p = pages[0];
        var label = new TextBlock
        {
            Text = $"缩放：{preferences.Scale:P0}",
            Margin = new Thickness(0, 10, 0, 4)
        };
        p.Children.Add(label);
        var scale = new Slider
        {
            Minimum = .5,
            Maximum = 2.0,
            Value = preferences.Scale,
            TickFrequency = .05,
            IsSnapToTickEnabled = true
        };
        scale.ValueChanged += (_, _) =>
        {
            preferences.Scale = scale.Value;
            label.Text = $"缩放：{scale.Value:P0}";
            ApplyCompanionScale();
        };
        p.Children.Add(scale);
        p = pages[1];
        var bubbleScaleLabel = new TextBlock
        {
            Text = $"文字气泡大小：{preferences.BubbleScale:P0}",
            Margin = new Thickness(0, 18, 0, 4)
        };
        p.Children.Add(bubbleScaleLabel);
        var bubbleScale = new Slider
        {
            Minimum = .5, Maximum = 2.0, Value = preferences.BubbleScale,
            TickFrequency = .05, IsSnapToTickEnabled = true
        };
        System.Windows.Automation.AutomationProperties.SetName(bubbleScale, "文字气泡大小");
        bubbleScale.ValueChanged += (_, _) =>
        {
            preferences.BubbleScale = bubbleScale.Value;
            bubbleScaleLabel.Text = $"文字气泡大小：{bubbleScale.Value:P0}";
            ApplyCompanionBubbleScale();
        };
        p.Children.Add(bubbleScale);
        p = pages[0];
        var intervalLabel = new TextBlock { Text = $"动作触发间隔：{preferences.AnimationIntervalSeconds} 秒", Margin = new Thickness(0, 18, 0, 4) };
        p.Children.Add(intervalLabel);
        var interval = new Slider { Minimum = 30, Maximum = 300, Value = preferences.AnimationIntervalSeconds, TickFrequency = 10, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(interval, "动作触发间隔");
        interval.ValueChanged += (_, _) =>
        {
            preferences.AnimationIntervalSeconds = (int)Math.Round(interval.Value);
            intervalLabel.Text = $"动作触发间隔：{preferences.AnimationIntervalSeconds} 秒";
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
            SaveCompanion();
        };
        p.Children.Add(interval);
        var meals = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        void MealTime(string labelText, int initialHour, Action<int> change)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 110 };
            column.Children.Add(new TextBlock { Text = labelText, Margin = new Thickness(0, 0, 0, 6) });
            var selector = new ComboBox();
            for (int hour = 0; hour < 24; hour++) selector.Items.Add($"{hour:00}:00");
            selector.SelectedIndex = initialHour;
            selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) { change(selector.SelectedIndex); SaveCompanion(); } };
            column.Children.Add(selector); meals.Children.Add(column);
        }
        MealTime("早餐时间", preferences.BreakfastHour, h => preferences.BreakfastHour = h);
        MealTime("午餐时间", preferences.LunchHour, h => preferences.LunchHour = h);
        MealTime("晚餐时间", preferences.DinnerHour, h => preferences.DinnerHour = h);
        p.Children.Add(meals);
        p = pages[1];
        var freq = new ComboBox
        {
            Margin = new Thickness(0, 8, 0, 8)
        };
        foreach (var range in CompanionPreferences.SpeechRanges)
            freq.Items.Add($"说话间隔：{range.Min}–{range.Max} 分钟");
        freq.SelectedIndex = preferences.SpeechRangeIndex;
        freq.SelectionChanged += (_, _) =>
        {
            if (freq.SelectedIndex < 0) return;
            preferences.SpeechMinMinutes = CompanionPreferences.SpeechRanges[freq.SelectedIndex].Min;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            SaveCompanion();
        };
        p.Children.Add(freq);
        var dwellLabel = new TextBlock
        {
            Text = "气泡驻留时间：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex],
            Margin = new Thickness(0, 8, 0, 4)
        };
        p.Children.Add(dwellLabel);
        var dwell = new Slider
        {
            Minimum = 0,
            Maximum = 7,
            Value = preferences.BubbleDurationIndex,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 1,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            ToolTip = dwellLabel.Text
        };
        System.Windows.Automation.AutomationProperties.SetName(dwell, "气泡驻留时间");
        dwell.ValueChanged += (_, _) =>
        {
            preferences.BubbleDurationIndex = (int)Math.Round(dwell.Value);
            dwellLabel.Text = "气泡驻留时间：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex];
            dwell.ToolTip = dwellLabel.Text;
            if (bubble.Visibility == Visibility.Visible)
                ResetBubbleDeadline();
            SaveCompanion();
        };
        p.Children.Add(dwell);
        p = pages[2];
        var installModel = new Button { Content = "安装模型", Margin = new Thickness(0, 8, 0, 8) };
        installModel.Click += (_, _) => OpenModelInstaller();
        p.Children.Add(installModel);
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 8, 0, 8)
        };
        p.Children.Add(status);
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        void UpdateStatus()
        {
            bool local = ModelStore.Resolve(Environment.CurrentDirectory) != null;
            var cloud = CompanionCloud.TryLoad();
            installModel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            string detail = preferences.CloudSelected
                ? "云端模型：" + (cloud?.Model ?? "未配置 Key") + "\n" + brain.CloudResult(cloud)
                : "本地模型：" + (local ? brain.Status : "尚未安装");
            status.Text = (brain.CanRunOnCurrentPower ? (CompanionNative.OnAC ? "已插电" : "电池供电 · 允许当前模式运行") : brain.PowerPauseReason)
                + "\n" + detail + "\n运行状态：" + brain.Status;

        }
        AddCloudSettings(p, UpdateStatus);
        timer.Tick += (_, _) => UpdateStatus();
        UpdateStatus();
        timer.Start();
        void Button(string text, Action action)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };
            b.Click += (_, _) => action();
            p.Children.Add(b);
        }

        p = pages[0];
        Button("检查更新", async () => await CheckForUpdates(true));
        var updates = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        p.Children.Add(updates);
        timer.Tick += (_, _) => updates.Text = updateStatus;
        Button("隐藏到任务栏通知区", HideCompanion);
        Button("回到右下角", () =>
        {
            var a = SystemParameters.WorkArea;
            Left = a.Right - Width - 8;
            Top = a.Bottom - Height + 12;
            ClampCompanion();
            anchor = new Point(Left, Top);
            SaveCompanion();
        });
        Button("关闭桌宠", () =>
        {
            w.Close();
            base.Close();
        });
        Action? disposeMcpSettings = pages.Length > 3 ? AddMcpSettings(pages[3], w) : null;
        foreach (var panel in pages) Card(panel);
        disposeCompanionSettingsContent = () => { timer.Stop(); disposeMcpSettings?.Invoke(); };
        refreshCompanionSettings = () => BuildCompanionSettingsContent(w, selectedPage, views.Select(v => v.VerticalOffset).ToArray());
        if (scrollOffsets != null) w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        {
            if (companionSettings != w || w.Content != shell) return;
            for (int i = 0; i < views.Length; i++) views[i].ScrollToVerticalOffset(scrollOffsets[i]);
        }));
    }

    private void AddCloudSettings(StackPanel panel, Action refreshStatus)
    {
        var editor = CompanionCloud.LoadEditor();
        panel.Children.Add(new TextBlock { Text = "线上接口", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        panel.Children.Add(new TextBlock
        {
            Text = "选择云端模式后使用这里的接口。Key 保存在本机，不会从 MCP 设置里读出。测试连接会发送固定短句并产生少量调用费用，不读取屏幕。",
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var sources = new TextBlock { Text = editor.Sources + "\n" + editor.Warning, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        panel.Children.Add(sources);
        TextBox Input(string name, string value)
        {
            panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 6, 0, 0) });
            var box = new TextBox
            {
                Text = value,
                MinHeight = 36,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 229, 242)),
                Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
            };
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            panel.Children.Add(box);
            return box;
        }
        var baseUrl = Input("接口地址", editor.BaseUrl);
        var model = Input("模型", editor.Model);
        panel.Children.Add(new TextBlock { Text = "API Key", Margin = new Thickness(0, 6, 0, 0) });
        var key = new PasswordBox
        {
            Password = editor.ApiKey,
            MinHeight = 36,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 229, 242)),
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 253))
        };
        System.Windows.Automation.AutomationProperties.SetName(key, "API Key");
        panel.Children.Add(key);
        if (editor.EnvironmentOverridesKey)
            panel.Children.Add(new TextBlock { Text = "检测到环境变量 DEEPSEEK_API_KEY，它会盖过这里保存的 Key。", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var save = new Button { Content = "保存线上接口", Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        var message = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
        save.Click += (_, _) =>
        {
            try
            {
                CompanionCloud.SaveUser(baseUrl.Text, model.Text, key.Password);
                ApplyModelConfiguration();
                var effective = CompanionCloud.LoadEditor();
                baseUrl.Text = effective.BaseUrl; model.Text = effective.Model; key.Password = effective.ApiKey;
                sources.Text = effective.Sources + "\n" + effective.Warning;
                refreshStatus();
                message.Text = string.IsNullOrWhiteSpace(key.Password)
                    ? "已保存。没有有效 Key 时，云端模式不可用。"
                    : "已保存，生效配置见上方；模型模式保持不变。";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                message.Text = error.Message;
            }
        };
        panel.Children.Add(save);
        panel.Children.Add(message);
        var test = new Button { Content = "测试已保存的云端连接", Margin = new Thickness(0, 6, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        test.Click += async (_, _) => {
            test.IsEnabled = false;
            try { message.Text = "正在测试已保存的生效配置…"; message.Text = await brain.TestCloudConnection(); refreshStatus(); }
            finally { test.IsEnabled = true; }
        };
        panel.Children.Add(test);
    }

    private void ActivateCompanionSettings(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Show();
        window.Activate();
        // Pet/tray menus finish restoring their previous foreground window after
        // the click handler. Activate once that input/menu work has completed.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
        {
            if (companionSettings == window && window.IsVisible && !companionClosed && !exitAnimation)
            {
                // This is only reached by an explicit user request. Keep the
                // ordinary window band even if a native topmost flag was left over.
                window.Topmost = false;
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                CompanionNative.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                window.Activate();
            }
        }));
    }

}
