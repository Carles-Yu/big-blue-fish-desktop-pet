using BigBlueFish.DesktopHooks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private DesktopHookServer? desktopHookServer;
    private string? desktopHookEndpointPath;
    private string desktopHookStatus = "未启动";
    private string cursorHookStatus = "未写入";
    private string codexHookStatus = "未写入";
    private string geminiHookStatus = "未写入";
    private readonly TaskNoticeQueue taskNoticeQueue = new();
    private CompanionTaskNoticeOverlay? taskNoticeOverlay;

    private void StartDesktopHookListener()
    {
        if (!OperatingSystem.IsWindows() || desktopHookServer != null || companionClosed)
            return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigBlueFish", "desktop-hooks");
            Directory.CreateDirectory(dir);
            RestrictToCurrentUser(dir, true);

            var scriptSource = DesktopHookScriptSources().FirstOrDefault(File.Exists);
            var scriptDest = Path.Combine(dir, DesktopHookConfigInstaller.ScriptFileName);
            if (scriptSource != null)
            {
                File.Copy(scriptSource, scriptDest, true);
                RestrictToCurrentUser(scriptDest, false);
            }

            var server = DesktopHookServer.Start(reminder =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() => ShowDesktopHookReminder(reminder));
                }
                catch
                {
                }
            });
            desktopHookServer = server;
            desktopHookEndpointPath = Path.Combine(dir, "endpoint.json");
            WriteRestrictedText(desktopHookEndpointPath, JsonSerializer.Serialize(new
            {
                url = server.Url,
                token = server.Token,
                pid = Environment.ProcessId
            }));

            if (scriptSource != null)
                InstallDesktopHookConfigs(scriptDest);
            else
            {
                cursorHookStatus = "缺少钩子脚本，未改配置。";
                codexHookStatus = cursorHookStatus;
                geminiHookStatus = cursorHookStatus;
            }

            desktopHookStatus = $"已在 127.0.0.1:{server.Port} 监听";
        }
        catch
        {
            StopDesktopHookListener();
            desktopHookStatus = "接收口未能启动";
        }
    }

    private void StopDesktopHookListener()
    {
        try { desktopHookServer?.Dispose(); } catch { }
        desktopHookServer = null;
        if (!string.IsNullOrEmpty(desktopHookEndpointPath))
        {
            try { File.Delete(desktopHookEndpointPath); } catch { }
            desktopHookEndpointPath = null;
        }
        if (desktopHookStatus != "接收口未能启动")
            desktopHookStatus = "已停止";
    }

    private void StartTaskNoticeOverlay()
    {
        if (taskNoticeOverlay != null || companionClosed)
            return;
        taskNoticeOverlay = new CompanionTaskNoticeOverlay(taskNoticeQueue, () => preferences.ShowTaskCardsOnFullscreen);
    }

    private void StopTaskNoticeOverlay()
    {
        try { taskNoticeOverlay?.CloseOverlay(); } catch { }
        taskNoticeOverlay = null;
    }

    private void SyncTaskNoticeOverlay() => taskNoticeOverlay?.Sync();

    private void EnqueueTaskNotice(string tool, string? projectName, string status)
    {
        if (companionClosed || exitAnimation)
            return;
        StartTaskNoticeOverlay();
        taskNoticeQueue.Enqueue(tool, projectName, status);
        SyncTaskNoticeOverlay();
    }

    private void ShowDesktopHookReminder(DesktopHookReminder reminder)
    {
        if (companionClosed || exitAnimation)
            return;
        EnqueueTaskNotice(DesktopHookParser.AppName(reminder.Source), reminder.ProjectName, DesktopHookParser.ResultPhrase(reminder.Outcome));
        notifyIcon?.ShowBalloonTip(
            10000,
            reminder.Title.Length > 63 ? reminder.Title[..63] : reminder.Title,
            reminder.Message.Length > 240 ? reminder.Message[..240] + "…" : reminder.Message,
            reminder.Outcome == DesktopHookOutcome.Failed ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        if (!PuppetActive && !string.IsNullOrWhiteSpace(reminder.AnimationId))
        {
            try
            {
                PlayAnimation(animationPolicy.Find(reminder.AnimationId), "External");
            }
            catch
            {
            }
        }
    }

    private void InstallDesktopHookConfigs(string scriptPath)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        cursorHookStatus = MergeDesktopHookFile(
            Path.Combine(profile, ".cursor", "hooks.json"),
            scriptPath,
            DesktopHookConfigInstaller.TryMergeCursor);
        codexHookStatus = MergeDesktopHookFile(
            Path.Combine(profile, ".codex", "hooks.json"),
            scriptPath,
            DesktopHookConfigInstaller.TryMergeCodex);
        if (codexHookStatus is "已创建。" or "已更新。" or "已写入，无需改动。")
            codexHookStatus += "需在 Codex 桌面 Settings → Hooks 中批准；尚未收到过事件。若批准后仍无事件，将标记为未接通，不会改用其他办法。";
        geminiHookStatus = MergeDesktopHookFile(
            Path.Combine(profile, ".gemini", "config", "hooks.json"),
            scriptPath,
            DesktopHookConfigInstaller.TryMergeGemini);
    }

    private static string MergeDesktopHookFile(
        string path,
        string scriptPath,
        TryMergeDesktopHook merge)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!merge(existing, scriptPath, out var updated, out var status))
                return "hooks.json 无法解析，已保持原文件。";
            if (status == "unchanged")
                return "已写入，无需改动。";
            WriteTextAtomic(path, updated);
            return status == "created" ? "已创建。" : "已更新。";
        }
        catch
        {
            return "写入失败。";
        }
    }

    private void AddDesktopHookSettings(StackPanel panel)
    {
        panel.Children.Add(new TextBlock { Text = "图形界面任务完成提醒", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        panel.Children.Add(new TextBlock
        {
            Text =
                "大肥鱼只接收本机当前用户、经官方钩子送来的任务结束事件，不会去看命令行窗口、日志或界面。接收口只绑在 127.0.0.1，令牌仅当前用户可读。" +
                Environment.NewLine + Environment.NewLine +
                "Gemini / Antigravity 可按官方路径区分桌面与命令行，并且只在完全空闲时提醒，命令行任务会丢弃。Cursor 与 Codex 的官方钩子还不能区分桌面和命令行，因此只要官方钩子报任务结束就会提醒，命令行里的任务也可能弹出。" +
                Environment.NewLine + Environment.NewLine +
                "任务结束提醒会在主显示器工作区左上角排成可堆积的小卡片，写明工具、项目和状态，最新的在最上面；点一张只消这一张，不会自动消失。同屏最多 8 张，更早的先收起，点掉前面的之后还会再出来。安静时段仍然出卡。MCP notify_task 也走同一条队列。任务状态不再写进鱼鱼身边的气泡，避免被后一条挤掉。" +
                Environment.NewLine + Environment.NewLine +
                "Codex 桌面需要在 Settings → Hooks 中批准后才会触发；若批准后仍没有事件，会标记为未接通，不会改用其他办法。" +
                Environment.NewLine + Environment.NewLine +
                "提醒文案会带上工具名、项目名，以及必要时的模型名，不会把对话原文放进卡片。动作按状态点播现成片段：做完用「工作状态-雀跃庆祝」，出错按 Id 点播「工作状态-垂头叹气冒汗」，取消用「点击回应-害羞惊讶」，结束了用「工作状态-清点归档」，被停下了用「点击回应-傲娇生气」。" +
                Environment.NewLine + Environment.NewLine +
                "接收口：" + desktopHookStatus + Environment.NewLine +
                "Cursor：" + cursorHookStatus + Environment.NewLine +
                "Codex：" + codexHookStatus + Environment.NewLine +
                "Gemini：" + geminiHookStatus,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap
        });
        var fullscreenCards = new CheckBox
        {
            Content = "其他应用全屏时显示通知卡片",
            IsChecked = preferences.ShowTaskCardsOnFullscreen,
            Margin = new Thickness(0, 8, 0, 0)
        };
        fullscreenCards.Click += (_, _) =>
        {
            preferences.ShowTaskCardsOnFullscreen = fullscreenCards.IsChecked == true;
            SaveCompanion();
            SyncTaskNoticeOverlay();
        };
        panel.Children.Add(fullscreenCards);
    }

    private delegate bool TryMergeDesktopHook(string? existingJson, string scriptPath, out string updatedJson, out string status);


    private static IEnumerable<string> DesktopHookScriptSources()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "hooks", DesktopHookConfigInstaller.ScriptFileName);
        yield return Path.Combine(AppContext.BaseDirectory, DesktopHookConfigInstaller.ScriptFileName);
    }


    private static void WriteRestrictedText(string path, string text)
    {
        WriteTextAtomic(path, text);
        RestrictToCurrentUser(path, false);
    }

    private static void WriteTextAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    private static void RestrictToCurrentUser(string path, bool directory)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
            return;
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }
}
