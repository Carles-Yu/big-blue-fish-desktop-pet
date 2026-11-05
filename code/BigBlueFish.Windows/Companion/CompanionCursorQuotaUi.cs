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
    private Window? cursorQuotaWindow;
    private void OpenCursorQuota()
    {
        if (companionClosed || exitAnimation) return;
        companionContextMenu.IsOpen = false;
        if (cursorQuotaWindow != null)
        {
            if (cursorQuotaWindow.WindowState == WindowState.Minimized) cursorQuotaWindow.WindowState = WindowState.Normal;
            cursorQuotaWindow.Activate(); return;
        }
        cursorQuotaWindow = new CompanionCursorQuotaWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        cursorQuotaWindow.Closed += (_, _) => cursorQuotaWindow = null;
        cursorQuotaWindow.Show();
    }
}

internal sealed class CompanionCursorQuotaWindow : CompanionQuotaWindow
{
    internal CompanionCursorQuotaWindow() : this(CompanionCursorQuota.Query) { }
    internal CompanionCursorQuotaWindow(Func<CancellationToken, Task<CursorQuotaReport>> query)
        : base(CompanionBrand.Cursor, "账户额度 · Auto / API", "只在打开或刷新时查询，不发起模型对话。\n重置时间按本机时区显示。", "cursor-quota-refresh", async token =>
        {
            var report = await query(token);
            return new QuotaViewReport(report.Success, report.Message,
                report.Lines.Select(line => new QuotaCard("", line.Title, line.RemainingPercent, line.ResetsAt, line.Note)).ToArray(), report.QueriedAt);
        }) { }
}
