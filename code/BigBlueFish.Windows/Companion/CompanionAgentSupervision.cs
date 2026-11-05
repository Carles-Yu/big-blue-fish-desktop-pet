#if !COMPANION_SAFE
using BigBlueFish.Control;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private CompanionAgentPrompt? agentPrompt;
    private bool AgentQuestionOpen => agentPrompt != null;
    private void CloseAgentQuestion() => agentPrompt?.Close();

    private object NotifyAgentTask(JsonElement args)
    {
        var title = ControlTools.RequiredText(args, "title", 120);
        var message = ControlTools.RequiredText(args, "message", 2000);
        var status = ControlTools.RequiredText(args, "status", 20);
        if (status is not ("completed" or "failed")) throw new ArgumentException("status 必须为 completed 或 failed。");
        ShowMcpSpeech($"{title}\n{(status == "completed" ? "已完成" : "未能完成")}：{message}");
        agentNotificationClick = RestoreCompanion;
        notifyIcon.ShowBalloonTip(10000, title.Length > 63 ? title[..63] : title, message.Length > 240 ? message[..240] + "…" : message,
            status == "completed" ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        return ControlTools.Text(new { accepted = true, status, userRead = false });
    }

    private async Task<object> AskAgentUser(JsonElement args, CancellationToken token)
    {
        var title = ControlTools.RequiredText(args, "title", 120);
        var question = ControlTools.RequiredText(args, "question");
        var options = Array.Empty<string>();
        if (args.TryGetProperty("options", out var choices))
        {
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 8) throw new ArgumentException("options 最多包含 8 个选项。");
            options = choices.EnumerateArray().Select(choice =>
                choice.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(choice.GetString()) && choice.GetString()!.Length <= 200
                    ? choice.GetString()! : throw new ArgumentException("每个选项必须为 1–200 字符的文字。")).ToArray();
            if (options.Distinct(StringComparer.Ordinal).Count() != options.Length) throw new ArgumentException("选项不能重复。");
        }
        int seconds = 300;
        if (args.TryGetProperty("timeout_seconds", out var timeout) && (!timeout.TryGetInt32(out seconds) || seconds < 15 || seconds > 600))
            throw new ArgumentException("timeout_seconds 范围为 15–600。");
        if (agentPrompt != null) throw new InvalidOperationException("鱼鱼正在等待另一个问题的回答，请稍后再问。");
        var window = new CompanionAgentPrompt(title, question, options) { Owner = this };
        agentPrompt = window;
        var area = CompanionEdgeLayout.WorkArea(this);
        window.Width = Math.Min(window.Width, area.Width - 16);
        window.Height = Math.Min(window.Height, area.Height - 16);
        var body = CompanionEdgeLayout.PetBounds(this);
        double x = Left + body.Right + 8;
        if (x + window.Width > area.Right - 8) x = Left + body.Left - window.Width - 8;
        window.Left = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.Width - 8));
        window.Top = Math.Clamp(Top + body.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.Height - 8));
        try
        {
            // Show without activating: a new Agent question must not steal typing from another app.
            window.Show();
            agentNotificationClick = () => { if (window.IsVisible) { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); } };
            notifyIcon.ShowBalloonTip(10000, title.Length > 63 ? title[..63] : title, "鱼鱼收到一个需要你回答的问题，点击查看。", System.Windows.Forms.ToolTipIcon.Info);
            try
            {
                var answer = await window.Answer.WaitAsync(TimeSpan.FromSeconds(seconds), token);
                token.ThrowIfCancellationRequested();
                return ControlTools.Text(answer);
            }
            catch (TimeoutException) { return ControlTools.Text(new AgentAnswer("expired", null, null, "")); }
        }
        finally
        {
            window.Close();
            if (agentPrompt == window) agentPrompt = null;
            agentNotificationClick = null;
        }
    }
}

internal sealed record AgentAnswer(string status, int? selectedIndex, string? selectedLabel, string text);

// Answers go only to the pending tool response, never to the shared interaction broadcast or pet memory.
internal sealed class CompanionAgentPrompt : Window
{
    private readonly TaskCompletionSource<AgentAnswer> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<AgentAnswer> Answer => completion.Task;

    internal CompanionAgentPrompt(string title, string question, string[] options)
    {
        Title = "鱼鱼 · 等你回答";
        Width = 440; Height = 610; MinWidth = 340; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Brushes.White; FontSize = 20; Foreground = new SolidColorBrush(Color.FromRgb(65, 85, 124));
        CompanionSettingsTheme.Apply(this, "鱼鱼帮你问问");
        var root = new Grid { Margin = new Thickness(20), Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.MidnightBlue });
        content.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 20, Margin = new Thickness(0, 12, 0, 12) });
        var radios = options.Select((label, i) => new RadioButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            GroupName = "answer", Style = (Style)FindResource("FishChoiceStyle"), Margin = new Thickness(0, 5, 0, 5), Tag = i }).ToArray();
        foreach (var radio in radios) content.Children.Add(radio);
        content.Children.Add(new TextBlock { Text = options.Length == 0 ? "填写你的回答" : "也可以直接填写，或补充说明", Margin = new Thickness(0, 12, 0, 6), Foreground = Brushes.SlateGray });
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 86, MaxHeight = 160,
            MaxLength = 4000, Padding = new Thickness(9), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(input, "agent-answer-text");
        content.Children.Add(input);
        content.Children.Add(new TextBlock { Text = "回答会发送给提出问题的 Agent。请勿填写密码或密钥。", TextWrapping = TextWrapping.Wrap,
            FontSize = 15, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 10) });
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var submit = new Button { Content = "发送给 Agent", IsEnabled = false, Padding = new Thickness(14, 7, 14, 7),
            Background = new SolidColorBrush(Color.FromRgb(69, 116, 225)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        submit.Style = (Style)FindResource("FishPrimaryButtonStyle");
        AutomationProperties.SetAutomationId(submit, "agent-answer-submit");
        void Refresh() => submit.IsEnabled = radios.Any(r => r.IsChecked == true) || !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => Refresh();
        foreach (var radio in radios) radio.Checked += (_, _) => Refresh();
        submit.Click += (_, _) =>
        {
            var selected = Array.FindIndex(radios, r => r.IsChecked == true);
            if (selected < 0 && string.IsNullOrWhiteSpace(input.Text)) return;
            completion.TrySetResult(new AgentAnswer("submitted", selected < 0 ? null : selected,
                selected < 0 ? null : options[selected], input.Text));
            Close();
        };
        footer.Children.Add(cancel); footer.Children.Add(submit);
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = CompanionSettingsTheme.Scene(root);
        Closed += (_, _) => completion.TrySetResult(new AgentAnswer("cancelled", null, null, ""));
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
}
#endif
