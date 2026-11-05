using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace BigBlueFish.Windows;

internal static class CompanionSettingsTheme
{
    internal static FontFamily CuteFont => new(new Uri("pack://application:,,,/BigBlueFish;component/"), "./Res/Font/#乐米小奶泡体");

    internal static void Apply(Window window, string caption = "鱼鱼的小小设置")
    {
        if (window.TryFindResource("FishSettingsWindowTemplate") == null)
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/BigBlueFish;component/Companion/CompanionSettingsStyles.xaml", UriKind.Relative) });
        window.Tag = caption;
        window.FontFamily = CuteFont;
        window.WindowStyle = WindowStyle.None;
        // Keep an ordinary opaque HWND: Windows still owns resizing, caption dragging and snapping.
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            // Match Windows' normal rounded HWND; the template paints square all
            // the way to its edges and lets the compositor clip the outer corners.
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false
        });
        window.Template = (System.Windows.Controls.ControlTemplate)window.FindResource("FishSettingsWindowTemplate");
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)));
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                int rounded = 2;
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 33, ref rounded, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        };
    }

    internal static System.Windows.Controls.Grid Scene(UIElement content)
    {
        var scene = new System.Windows.Controls.Grid();
        scene.Children.Add(new CompanionSettingsBackdrop { Opacity = .7 });
        scene.Children.Add(content);
        return scene;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
