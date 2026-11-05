using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BigBlueFish.Windows;

internal static class AgentUiVerification
{
    internal static int Run(string directory)
    {
        int result = 1;
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var windows = new List<Window>();
            int checks = 0;
            void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
            void Pump()
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            }
            IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is T value) yield return value;
                    foreach (var nested in Children<T>(child)) yield return nested;
                }
            }
            try
            {
                Directory.CreateDirectory(directory);
                var type = typeof(MainWindow).Assembly.GetType("BigBlueFish.Windows.CompanionAgentPrompt", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                Window Open(string[] options)
                {
                    var window = (Window)Activator.CreateInstance(type, flags, null,
                        new object[] { "蓝色大肥鱼 · 需要你决定", "修复已完成。接下来想怎么处理？\n请选择一项，或写下你的要求。", options }, null)!;
                    windows.Add(window); window.Show(); Pump(); return window;
                }
                Task Answer(Window window) => (Task)type.GetProperty("Answer", flags)!.GetValue(window)!;
                JsonElement Value(Task task) => JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
                System.Windows.Controls.Button Submit(Window w) => Children<System.Windows.Controls.Button>(w).Single(b => AutomationProperties.GetAutomationId(b) == "agent-answer-submit");
                var choice = Open(["先查看修改", "继续运行测试"]);
                var pending = Answer(choice);
                Check(!Submit(choice).IsEnabled && !pending.IsCompleted, "No default choice or automatic consent");
                var radios = Children<System.Windows.Controls.RadioButton>(choice).ToArray();
                radios[1].IsChecked = true; Pump();
                Check(Submit(choice).IsEnabled, "Selecting an option enables submission");
                var input = Children<System.Windows.Controls.TextBox>(choice).Single();
                input.Text = "只跑相关测试，完成后提醒我。\n原文 <> &"; Pump();
                FrameworkElement content = choice;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(directory, "agent-question.png"))) encoder.Save(stream);
                Submit(choice).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var answer = Value(pending);
                Check(answer.GetProperty("status").GetString() == "submitted", "Explicit submission succeeds");
                Check(answer.GetProperty("selectedIndex").GetInt32() == 1 && answer.GetProperty("selectedLabel").GetString() == "继续运行测试", "Selected option identity returned");
                Check(answer.GetProperty("text").GetString() == input.Text, "Literal multiline answer preserved");
                var cancelled = Open(["继续", "停止"]); var cancelTask = Answer(cancelled);
                cancelled.Close(); Pump();
                var cancellation = Value(cancelTask);
                Check(cancellation.GetProperty("status").GetString() == "cancelled" && cancellation.GetProperty("selectedIndex").ValueKind == JsonValueKind.Null,
                    "Closing never becomes approval");
                var free = Open([]); var freeTask = Answer(free);
                var text = Children<System.Windows.Controls.TextBox>(free).Single();
                text.Text = "  \n"; Pump(); Check(!Submit(free).IsEnabled, "Whitespace-only input cannot submit");
                text.Text = "请把按钮放在右边"; Pump();
                Submit(free).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var freeAnswer = Value(freeTask);
                Check(freeAnswer.GetProperty("status").GetString() == "submitted" && freeAnswer.GetProperty("selectedIndex").ValueKind == JsonValueKind.Null
                    && freeAnswer.GetProperty("text").GetString() == text.Text, "Free-form answer works without options");
                Console.WriteLine("AGENT-UI-OK " + checks); result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { foreach (var w in windows) w.Close(); app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
}

