using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BigBlueFish.Windows;

internal enum CompanionBrand { Codex, Cursor }

internal static class CompanionBrandIcons
{
    private static readonly Dictionary<CompanionBrand, ImageSource> sources = new();
    private static readonly Dictionary<CompanionBrand, System.Drawing.Bitmap> trayImages = new();

    internal static ImageSource Source(CompanionBrand brand)
    {
        if (sources.TryGetValue(brand, out var cached)) return cached;
        ImageSource source;
        if (brand == CompanionBrand.Cursor)
        {
            // Preserve the official 2D cube's geometry and original brand colour.
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/BigBlueFish;component/assets/brands/cursor.svg"))!.Stream;
            var svg = XDocument.Load(stream);
            XNamespace ns = "http://www.w3.org/2000/svg";
            var drawing = new DrawingGroup();
            foreach (var path in svg.Descendants(ns + "path"))
                drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(38, 37, 30)), null, Geometry.Parse(path.Attribute("d")!.Value)));
            source = new DrawingImage(drawing);
        }
        else
            source = new BitmapImage(new Uri("pack://application:,,,/BigBlueFish;component/assets/brands/codex.png"));
        source.Freeze(); sources.Add(brand, source); return source;
    }

    internal static System.Windows.Controls.Image Create(CompanionBrand brand, double size = 17) => new()
    {
        Source = Source(brand), Width = size, Height = size, Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true
    };

    internal static System.Drawing.Bitmap Tray(CompanionBrand brand)
    {
        if (trayImages.TryGetValue(brand, out var image)) return image;
        var source = Source(brand);
        double ratio = Math.Min(64 / source.Width, 64 / source.Height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(source, new Rect((64 - source.Width * ratio) / 2, (64 - source.Height * ratio) / 2, source.Width * ratio, source.Height * ratio));
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var decoded = new System.Drawing.Bitmap(stream);
        image = new System.Drawing.Bitmap(decoded); trayImages.Add(brand, image); return image;
    }
}
