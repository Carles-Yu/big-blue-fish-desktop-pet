using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private Window? codexQuotaWindow;
    private void OpenCodexQuota()
    {
        if (companionClosed || exitAnimation) return;
        companionContextMenu.IsOpen = false;
        if (codexQuotaWindow != null)
        {
            if (codexQuotaWindow.WindowState == WindowState.Minimized) codexQuotaWindow.WindowState = WindowState.Normal;
            codexQuotaWindow.Activate(); return;
        }
        codexQuotaWindow = new CompanionCodexQuotaWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        codexQuotaWindow.Closed += (_, _) => codexQuotaWindow = null;
        codexQuotaWindow.Show();
    }
}

internal sealed class CompanionCodexQuotaWindow : CompanionQuotaWindow
{
    internal CompanionCodexQuotaWindow() : this(CompanionCodexQuota.Query) { }
    internal CompanionCodexQuotaWindow(Func<CancellationToken, Task<CodexQuotaReport>> query)
        : base(CompanionBrand.Codex, "账户额度 · 使用窗口", "只在打开或刷新时查询，不发起模型对话。\n重置时间按本机时区显示。", "codex-quota-refresh", async token =>
        {
            var report = await query(token);
            return new QuotaViewReport(report.Success, report.Message,
                report.Buckets.SelectMany(bucket => bucket.Windows.Select(window =>
                    new QuotaCard(report.Buckets.Length > 1 ? bucket.Name : "", window.DurationLabel,
                        window.RemainingPercent, window.ResetsAt))).ToArray(), report.QueriedAt);
        }) { }
}
