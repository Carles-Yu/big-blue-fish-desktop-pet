using BigBlueFish.DesktopHooks;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace BigBlueFish.Windows;

internal sealed class CompanionTaskNoticeOverlay : Window
{
    private const double EdgeMargin = 16;
    private const double CardWidth = 260;
    private readonly TaskNoticeQueue queue;
    private readonly Func<bool> showOnFullscreen;
    private readonly StackPanel stack = new();

    internal CompanionTaskNoticeOverlay(TaskNoticeQueue queue, Func<bool> showOnFullscreen)
    {
        this.queue = queue;
        this.showOnFullscreen = showOnFullscreen;
        Title = "蓝色大肥鱼 · 任务通知";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = CardWidth + 8;
        SizeToContent = SizeToContent.Height;
        Content = new Border { Padding = new Thickness(0), Child = stack, Background = Brushes.Transparent };
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        LocationChanged += (_, _) => ApplyNoActivateStyle();
    }


    internal void Sync()
    {
        stack.Children.Clear();
        foreach (var card in queue.Visible)
            stack.Children.Add(CreateCard(card));

        var show = queue.Visible.Count > 0 && TaskNoticeQueue.ShouldShowOverlay(CompanionNative.IsFullscreen(), showOnFullscreen());
        if (!show)
        {
            if (IsVisible)
                Hide();
            return;
        }

        PositionOnPrimaryWorkArea();
        if (!IsVisible)
            Show();
        else
            ApplyNoActivateStyle();
        Topmost = true;
    }

    internal void CloseOverlay()
    {
        try { Close(); } catch { }
    }

    private UIElement CreateCard(TaskNoticeCard card)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = card.Tool,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 54, 89)),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(card.ProjectName))
        {
            body.Children.Add(new TextBlock
            {
                Text = card.ProjectName,
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(90, 108, 140)),
                TextWrapping = TextWrapping.Wrap
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = card.Status,
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(55, 80, 130)),
            TextWrapping = TextWrapping.Wrap
        });

        var border = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)),
            BorderThickness = new Thickness(1),
            Child = body,
            Cursor = Cursors.Hand,
            Focusable = false,
            Tag = card.Id
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (border.Tag is string id)
                queue.Dismiss(id);
            Sync();
        };
        return border;
    }

    private void PositionOnPrimaryWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Rect area;
        if (CompanionNative.TryGetPrimaryMonitorWorkArea(out var primary))
            area = new Rect(primary.Left / dpi.DpiScaleX, primary.Top / dpi.DpiScaleY, (primary.Right - primary.Left) / dpi.DpiScaleX, (primary.Bottom - primary.Top) / dpi.DpiScaleY);
        else
            area = SystemParameters.WorkArea;
        Left = area.Left + EdgeMargin;
        Top = area.Top + EdgeMargin;
    }

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        CompanionNative.SetStyle(hwnd, -20, new IntPtr(CompanionNative.GetStyle(hwnd, -20).ToInt64() | 0x08000000L | 0x80L));
    }
}
