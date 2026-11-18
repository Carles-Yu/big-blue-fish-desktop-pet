using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private Window? geminiQuotaWindow;
    private void OpenGeminiQuota()
    {
        if (companionClosed || exitAnimation) return;
        companionContextMenu.IsOpen = false;
        if (geminiQuotaWindow != null)
        {
            if (geminiQuotaWindow.WindowState == WindowState.Minimized) geminiQuotaWindow.WindowState = WindowState.Normal;
            geminiQuotaWindow.Activate(); return;
        }
        geminiQuotaWindow = new CompanionGeminiQuotaWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        geminiQuotaWindow.Closed += (_, _) => geminiQuotaWindow = null;
        geminiQuotaWindow.Show();
    }
}

internal sealed class CompanionGeminiQuotaWindow : CompanionQuotaWindow
{
    internal CompanionGeminiQuotaWindow() : this(CompanionGeminiQuota.Query) { }
    internal CompanionGeminiQuotaWindow(Func<CancellationToken, Task<GeminiQuotaReport>> query)
        : base(CompanionBrand.Gemini, "账户额度 · 模型组与积分", "只在打开或刷新时查询，不发起模型对话。\n重置时间按本机时区显示。", "gemini-quota-refresh", async token =>
        {
            var report = await query(token);
            return CompanionGeminiQuota.ToView(report);
        }) { }
}
