using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.Windows;

// Decorative background only: scales with the client area, never intercepts input or invokes a model.
internal sealed class CompanionSettingsBackdrop : FrameworkElement
{
    private readonly BitmapImage fish = new(new Uri("pack://application:,,,/BigBlueFish;component/assets/settings/fish-rest.png"));

    internal CompanionSettingsBackdrop()
    {
        IsHitTestVisible = false; Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(233, 243, 252), Color.FromRgb(241, 245, 255), 35), null, new Rect(0, 0, width, height));
        var lavender = new RadialGradientBrush(Color.FromArgb(155, 191, 199, 240), Colors.Transparent);
        drawing.DrawEllipse(lavender, null, new Point(width * .92, height * .04), width * .65, height * .64);
        var mint = new RadialGradientBrush(Color.FromArgb(140, 207, 237, 228), Colors.Transparent);
        drawing.DrawEllipse(mint, null, new Point(width * .35, height * .55), width * .43, height * .48);
        var glow = new RadialGradientBrush(Color.FromArgb(150, 255, 255, 255), Colors.Transparent);
        drawing.DrawEllipse(glow, null, new Point(width * .72, height * .70), width * .45, height * .40);

        double size = Math.Min(610, Math.Max(390, height * .92));
        drawing.PushOpacity(.12);
        drawing.DrawImage(fish, new Rect(width - size * .72, height - size * .87, size, size));
        drawing.Pop();

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 147, 182, 225)), 1);
        foreach (var point in new[] { new Point(.09, .71), new Point(.28, .16), new Point(.48, .80), new Point(.68, .12), new Point(.85, .34), new Point(.95, .88) })
            drawing.DrawEllipse(null, pen, new Point(width * point.X, height * point.Y), 2, 2);
        drawing.Pop();
    }
}
