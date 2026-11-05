using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BigBlueFish.Memory;

namespace BigBlueFish.Windows;

internal sealed partial class LinuxPetWindow
{
    CompanionMemory? companionMemory;
    CancellationTokenSource? memorySyncLoop;
    Window? memoryWindow;
    Window? chatWindow;

    CompanionMemory EnsureLinuxMemory()
    {
        companionMemory ??= CompanionMemory.Open();
        companionMemory.Enabled = Preferences.MemoryEnabled;
        companionMemory.SyncEnabled = Preferences.SyncEnabled;
        companionMemory.ServerUrl = Preferences.SyncServerUrl;
        if (memorySyncLoop == null)
        {
            memorySyncLoop = new CancellationTokenSource();
            _ = RunLinuxMemorySync(memorySyncLoop.Token);
        }
        return companionMemory;
    }

    async Task RunLinuxMemorySync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60 + Random.Shared.Next(0, 20)), token);
                if (companionMemory is { Enabled: true, SyncEnabled: true })
                    try { await companionMemory.SyncNow(); } catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    void OpenLinuxMemory()
    {
        if (memoryWindow != null) { memoryWindow.Activate(); return; }
        CompanionMemory memory;
        try { memory = EnsureLinuxMemory(); }
        catch (Exception error) { _ = Confirm("记忆还没准备好", error.Message); return; }
        var status = Paragraph("");
        var list = new ListBox { Height = 160 };
        var editor = new TextBox { AcceptsReturn = true, MinHeight = 64, TextWrapping = TextWrapping.Wrap };
        var localOnly = new CheckBox { Content = "只保存在这台电脑" };
        var allowCloud = new CheckBox { Content = "可以放进云端模型" };
        var records = new List<MemoryRecord>();
        void Reload()
        {
            var keepSelection = list.SelectedIndex >= 0 && list.SelectedIndex < records.Count ? records[list.SelectedIndex].Id : (Guid?)null;
            records = memory.List().ToList();
            list.ItemsSource = records.Select(item => item.Content).ToArray();
            if (keepSelection is Guid selectedId)
            {
                list.SelectedIndex = records.FindIndex(item => item.Id == selectedId);
                if (list.SelectedIndex < 0) editor.Text = "";
            }
            if (memory.Conflicts().Count > 0) status.Text = $"有 {memory.Conflicts().Count} 条两边都改过，本地内容还留着。";
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= records.Count) return;
            var item = records[list.SelectedIndex];
            editor.Text = item.Content;
            localOnly.IsChecked = item.Scope == SyncScope.LocalOnly;
            allowCloud.IsChecked = item.AllowCloudModel;
            var conflict = memory.Conflicts().FirstOrDefault(row => row.RecordId == item.Id);
            if (conflict != null) status.Text = "这条记忆有冲突。云端文字：" + conflict.RemoteContent;
        };
        var pairing = new TextBox();
        var transfer = new TextBox();
        var root = new StackPanel();
        root.Children.Add(Paragraph("屏幕、新闻和 Agent 的话不会写进这里。"));
        root.Children.Add(list);
        root.Children.Add(editor);
        root.Children.Add(localOnly);
        root.Children.Add(allowCloud);
        void Act(string label, Func<Task> action)
        {
            var button = ActionButton(label, () => { });
            button.Click += async (_, _) =>
            {
                try { await action(); Reload(); }
                catch (Exception error) { status.Text = error.Message; }
            };
            root.Children.Add(button);
        }
        Act("记下", () => { if (!Preferences.MemoryEnabled) throw new InvalidOperationException("先打开本地记忆。"); memory.Remember(editor.Text ?? "", localOnly.IsChecked == true, allowCloud.IsChecked == true); editor.Text = ""; return Task.CompletedTask; });
        Act("保存修改", () =>
        {
            if (list.SelectedIndex < 0) throw new InvalidOperationException("先选一条。");
            memory.Update(records[list.SelectedIndex].Id, editor.Text ?? "", localOnly.IsChecked == true, allowCloud.IsChecked == true);
            return Task.CompletedTask;
        });
        Act("删除", () =>
        {
            if (list.SelectedIndex < 0) throw new InvalidOperationException("先选一条。");
            memory.Delete(records[list.SelectedIndex].Id);
            return Task.CompletedTask;
        });
        void Resolve(bool cloud)
        {
            if (list.SelectedIndex < 0) throw new InvalidOperationException("先选择有冲突的记忆。");
            memory.ResolveConflict(records[list.SelectedIndex].Id, cloud);
            status.Text = "已选择要保留的文字，等待同步确认。";
        }
        Act("采用本地文字解决冲突", () => { Resolve(false); return Task.CompletedTask; });
        Act("采用云端文字解决冲突", () => { Resolve(true); return Task.CompletedTask; });
        root.Children.Add(new TextBlock { Text = "配对码" });
        root.Children.Add(pairing);
        Act("配对这台设备", async () => status.Text = await memory.Pair(pairing.Text ?? "", Environment.MachineName));
        Act("第一台设备：创建记忆档案", () => { memory.CreateArchive(); status.Text = memory.Status; return Task.CompletedTask; });
        root.Children.Add(new TextBlock { Text = "传递口令" });
        root.Children.Add(transfer);
        Act("发布传递口令", async () => { await memory.PublishEnvelope(transfer.Text ?? ""); status.Text = memory.Status; });
        Act("导入传递口令", async () => { await memory.RedeemEnvelope(transfer.Text ?? ""); status.Text = memory.Status; });
        Act("立即同步", async () => status.Text = await memory.SyncNow());
        Act("接受云端重置", () => { memory.AcceptCloudReset(); status.Text = memory.Status; return Task.CompletedTask; });
        root.Children.Add(status);
        Reload();
        memoryWindow = Dialog("鱼鱼的记忆", new ScrollViewer { Content = root }, 640, 680);
        memoryWindow.Closed += (_, _) => memoryWindow = null;
        memoryWindow.Show();
    }

    void OpenLinuxChat()
    {
        if (chatWindow != null) { chatWindow.Activate(); return; }
        var transcript = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 240 };
        var input = new TextBox { AcceptsReturn = true, MinHeight = 64, TextWrapping = TextWrapping.Wrap };
        var state = Paragraph("这是桌宠自己的对话，不会发给外部 Agent。");
        var history = new List<(string Role, string Content)>();
        try
        {
            if (Preferences.MemoryEnabled)
                history.AddRange(EnsureLinuxMemory().Recent().Select(item => (item.Role, item.Content)));
        }
        catch (Exception error) { state.Text = error.Message; }
        void Paint() => transcript.Text = string.Join('\n', history.Select(item => (item.Role == "user" ? "我：" : "鱼鱼：") + item.Content));
        Paint();
        var send = ActionButton("发送", () => { });
        send.Click += async (_, _) =>
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length == 0 || brain.Busy) { if (brain.Busy) state.Text = "鱼鱼正在说话。"; return; }
            if (!brain.CanRunOnCurrentPower) { state.Text = brain.PowerPauseReason; return; }
            input.Text = "";
            if (text.StartsWith("记住") || text.StartsWith("请记住"))
            {
                if (!Preferences.MemoryEnabled) { state.Text = "记忆还关着。"; return; }
                var fact = (text.StartsWith("请记住") ? text["请记住".Length..] : text["记住".Length..]).TrimStart('，', ',', '：', ':', ' ');
                try
                {
                    EnsureLinuxMemory().Remember(fact, false, false);
                    history.Add(("user", text));
                    history.Add(("assistant", "记住了。"));
                    Paint();
                    state.Text = "记住了。";
                }
                catch (Exception error) { state.Text = error.Message; }
                return;
            }
            history.Add(("user", text));
            Paint();
            send.IsEnabled = false;
            try
            {
                var prior = history.Take(history.Count - 1).ToArray();
                var reply = await brain.ChatReply(text, prior, default);
                if (string.IsNullOrWhiteSpace(reply)) reply = "鱼鱼这次没有说出话。";
                history.Add(("assistant", reply));
                if (Preferences.MemoryEnabled)
                {
                    var memory = EnsureLinuxMemory();
                    memory.AddChat("user", text);
                    memory.AddChat("assistant", reply);
                }
                Paint();
                state.Text = brain.Status;
            }
            catch (Exception error) { state.Text = error.Message; }
            finally { send.IsEnabled = true; }
        };
        var root = new StackPanel();
        root.Children.Add(transcript);
        root.Children.Add(input);
        root.Children.Add(send);
        root.Children.Add(state);
        chatWindow = Dialog("与鱼鱼聊天", root, 520, 520);
        chatWindow.Closed += (_, _) => chatWindow = null;
        chatWindow.Show();
    }
}
