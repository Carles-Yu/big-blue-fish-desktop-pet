using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

internal sealed record QuotaCard(string Group, string Title, double? Remaining, DateTimeOffset? Reset, string? Note = null);
internal sealed record QuotaViewReport(bool Success, string Message, QuotaCard[] Cards, DateTimeOffset QueriedAt);

// Shared presentation only; provider authentication and data interpretation stay in their own clients.
internal class CompanionQuotaWindow : Window
{
    private static SolidColorBrush Ink => new(Color.FromRgb(39, 51, 77));
    private static SolidColorBrush Muted => new(Color.FromRgb(116, 129, 151));
    private static SolidColorBrush Accent => new(Color.FromRgb(69, 116, 225));
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel cards = new();
    private readonly TextBlock status = new() { Text = "正在连接账户…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock updated = new() { Text = "尚未更新", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border statusBox;
    private readonly Button refresh;
    private readonly Func<CancellationToken, Task<QuotaViewReport>> query;
    private bool busy, closed, hasData;

    internal CompanionQuotaWindow(CompanionBrand brand, string subtitle, string hint, string automationId,
        Func<CancellationToken, Task<QuotaViewReport>> query)
    {
        this.query = query;
        Title = $"鱼鱼 · {brand} 额度"; Width = 470; Height = 640; MinWidth = 390; MinHeight = 440;
        CompanionSettingsTheme.Apply(this, $"鱼鱼的 {brand} 小账本");
        Foreground = Ink;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 253));
        ShowInTaskbar = false; UseLayoutRounding = true;
        var root = new Grid { Margin = new Thickness(24), Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(15), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 234, 244)), BorderThickness = new Thickness(1), Child = CompanionBrandIcons.Create(brand, 28) });
        var identity = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock { Text = brand.ToString(), FontSize = 24, FontWeight = FontWeights.SemiBold });
        identity.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(identity, 1); header.Children.Add(identity); top.Children.Add(header);
        statusBox = new Border { Background = new SolidColorBrush(Color.FromRgb(234, 240, 252)), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 19, 0, 0), Child = status };
        status.Foreground = Accent; top.Children.Add(statusBox); root.Children.Add(top);
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 16, 0, 16), Padding = new Thickness(0, 0, 3, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var bottom = new StackPanel();
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(updated);
        refresh = new Button { Content = "↻  刷新额度", Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 10, 16, 10), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 12 };
        refresh.Style = (Style)FindResource("FishPrimaryButtonStyle");
        AutomationProperties.SetAutomationId(refresh, automationId);
        refresh.Click += async (_, _) => await Refresh(); Grid.SetColumn(refresh, 1); actions.Children.Add(refresh); bottom.Children.Add(actions);
        bottom.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 10.5,
            LineHeight = 17, Margin = new Thickness(0, 13, 0, 0) });
        Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = CompanionSettingsTheme.Scene(root);
        ShowEmpty("正在读取额度", "稍等一下，鱼鱼去看看。 ");
        Loaded += async (_, _) => await Refresh();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }

    private void ShowEmpty(string title, string message)
    {
        cards.Children.Clear();
        var text = new StackPanel { Margin = new Thickness(10, 32, 10, 32) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
        text.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        cards.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(15), Child = text });
    }

    private async Task Refresh()
    {
        if (busy || closed) return;
        busy = true; refresh.IsEnabled = false; refresh.Content = "正在查询…";
        SetStatus("正在查询账户额度…", false);
        try
        {
            var report = await query(lifetime.Token);
            if (closed) return;
            if (!report.Success) { Fail(report.Message); return; }
            hasData = report.Cards.Length > 0;
            SetStatus(hasData ? "●  额度已更新" : report.Message, false);
            status.ToolTip = report.Message;
            updated.Text = "更新于 " + report.QueriedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
            cards.Children.Clear();
            foreach (var card in report.Cards) cards.Children.Add(BuildCard(card));
            if (!hasData) ShowEmpty("暂无额度数据", "账户没有返回可展示的额度，请稍后刷新。");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) Fail("查询未完成，请稍后重试。"); }
        finally { busy = false; if (!closed) { refresh.IsEnabled = true; refresh.Content = "↻  刷新额度"; } }
    }
    private void Fail(string message)
    {
        status.ToolTip = null;
        SetStatus(message + (hasData ? "\n下方为上次结果，尚未刷新。" : ""), true);
        if (!hasData) ShowEmpty("暂时没查到", "请查看上方提示，处理后再刷新。");
    }
    private void SetStatus(string text, bool warning)
    {
        status.Text = text;
        status.Foreground = warning ? new SolidColorBrush(Color.FromRgb(158, 93, 35)) : Accent;
        statusBox.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 243, 225) : Color.FromRgb(234, 240, 252));
    }
    private static Border BuildCard(QuotaCard card)
    {
        var body = new StackPanel();
        var title = new Grid(); title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(card.Group) ? card.Title : card.Group + " · " + card.Title,
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        bool low = card.Remaining is <= 20;
        var tone = card.Remaining == null ? Color.FromRgb(116, 129, 151) : low ? Color.FromRgb(194, 118, 44) : Color.FromRgb(60, 135, 115);
        var badge = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(card.Remaining == null ? Color.FromRgb(239, 242, 247) : low ? Color.FromRgb(255, 242, 223) : Color.FromRgb(232, 246, 240)),
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = card.Remaining == null ? "未知" : card.Remaining <= 0 ? "已用尽" : low ? "余量较低" : "可用",
                FontSize = 10, Foreground = new SolidColorBrush(tone) } };
        Grid.SetColumn(badge, 1); title.Children.Add(badge); body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = card.Remaining is { } p ? $"剩余 {p:0.#}%" : card.Note ?? "剩余额度未知",
            FontSize = card.Remaining == null ? 22 : 31, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 12), TextWrapping = TextWrapping.Wrap });
        if (card.Remaining is { } percent)
        {
            double value = Math.Clamp(percent, 0, 100);
            var track = new Grid { Height = 7 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - value, GridUnitType.Star) });
            var fill = new Border { Background = new SolidColorBrush(low ? Color.FromRgb(222, 157, 79) : Color.FromRgb(98, 139, 232)), CornerRadius = new CornerRadius(4) };
            if (value > 0) track.Children.Add(fill);
            body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(235, 240, 249)), CornerRadius = new CornerRadius(4), Child = track });
        }
        body.Children.Add(new TextBlock { Text = card.Reset is { } reset ? "重置时间：" + reset.ToLocalTime().ToString("MM-dd HH:mm") : "重置时间未知",
            Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
        return new Border { Background = new SolidColorBrush(Color.FromArgb(222, 255, 255, 255)), CornerRadius = new CornerRadius(18), BorderBrush = new SolidColorBrush(Color.FromRgb(231, 236, 246)),
            BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 12), Child = body };
    }
}
