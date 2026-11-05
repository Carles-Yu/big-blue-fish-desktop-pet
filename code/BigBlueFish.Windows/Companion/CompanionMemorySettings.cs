using BigBlueFish.Memory;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private void AddMemorySettings(StackPanel panel)
    {
        static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
        var ink = Color("#263C61");
        var muted = Color("#7B8AA2");
        var blue = Color("#527AC5");
        TextBlock Text(string value, double size = 13, bool strong = false) => new()
        {
            Text = value, FontSize = size + 4, Foreground = strong ? ink : muted,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
        };
        var status = Text("只记住你想让鱼鱼记住的事。", 12);
        var statusFrame = new Border { Background = Color("#EDF3FC"), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0), Child = status };
        void Show(string value) => status.Text = value;
        Action refresh = () => { };
        Action clear = () => { };
        Button MakeButton(string label, string id, Action? action = null, bool primary = false, bool danger = false)
        {
            var button = new Button
            {
                Content = label, FontSize = 16, Padding = new Thickness(13, 8, 13, 8),
                Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Background = primary ? blue : Brushes.White,
                Foreground = primary ? Brushes.White : danger ? Color("#B05C65") : ink,
                BorderBrush = primary ? blue : Color("#E1E8F2")
            };
            if (primary)
            {
                var frame = new FrameworkElementFactory(typeof(Border)); frame.Name = "PrimaryBody";
                frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
                frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
                frame.SetValue(Border.BorderThicknessProperty, new Thickness(1)); frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
                frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); frame.AppendChild(presenter);
                var primaryTemplate = new ControlTemplate(typeof(Button)) { VisualTree = frame };
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty, Color("#426BB7"), "PrimaryBody")); primaryTemplate.Triggers.Add(hover);
                var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, Color("#355B9B"), "PrimaryBody")); primaryTemplate.Triggers.Add(pressed);
                var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "PrimaryBody")); primaryTemplate.Triggers.Add(disabled);
                button.Template = primaryTemplate;
            }
            AutomationProperties.SetAutomationId(button, id);
            if (action != null) button.Click += (_, _) => { try { action(); } catch (Exception error) { Show(error.Message); } };
            return button;
        }
        Button AsyncButton(string label, string id, Func<Task> action, bool primary = false)
        {
            var button = MakeButton(label, id, primary: primary);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action(); refresh(); }
                catch (Exception error) { Show(error.Message); }
                finally { button.IsEnabled = true; }
            };
            return button;
        }
        Border Card(string title, StackPanel body, string? subtitle = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(Text(title, 14, true));
            if (subtitle != null) { var note = Text(subtitle, 12); note.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(note); }
            body.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(body);
            return new Border { Child = stack, Padding = new Thickness(16), Background = Color("#E6FFFFFF"), CornerRadius = new CornerRadius(13),
                BorderBrush = Color("#E4EAF3"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        }
        Border Input(TextBox input, string hint)
        {
            input.Padding = new Thickness(10, 9, 10, 9); input.BorderThickness = new Thickness(0);
            input.Background = Brushes.Transparent; input.Foreground = ink; input.FontSize = 17;
            var placeholder = Text(hint, 12); placeholder.Margin = new Thickness(11, 10, 11, 0); placeholder.IsHitTestVisible = false;
            placeholder.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (_, _) => placeholder.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
            var grid = new Grid(); grid.Children.Add(input); grid.Children.Add(placeholder);
            return new Border { Child = grid, Background = Color("#F8FAFE"), BorderBrush = Color("#E1E8F2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        }
        CheckBox Toggle(string label, bool value, Action<bool> changed)
        {
            var toggle = new CheckBox { Content = label, IsChecked = value, FontSize = 17 };
            toggle.Click += (_, _) => { try { changed(toggle.IsChecked == true); SaveCompanion(); refresh(); } catch (Exception error) { Show(error.Message); } };
            return toggle;
        }

        var memoryToggle = Toggle("让鱼鱼记住我", preferences.MemoryEnabled, value =>
        {
            preferences.MemoryEnabled = value;
            EnsureCompanionMemory().Enabled = value;
            Show(value ? "记忆已开启，可以添加你的小习惯了。" : "记忆已暂停，保存的内容仍会保留。");
        });
        memoryToggle.Margin = new Thickness(2, -5, 2, 10);
        panel.Children.Add(memoryToggle);
        var tabs = new Grid { Margin = new Thickness(0, 0, 0, 16), Background = Color("#EAF0F8") };
        var tabFrame = new Border { CornerRadius = new CornerRadius(10), Background = tabs.Background, Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 14), Child = tabs };
        tabs.Margin = new Thickness(0);
        var pages = new[] { new StackPanel(), new StackPanel(), new StackPanel() };
        var tabButtons = new Button[3];
        var pageHost = new Grid();
        for (var i = 0; i < 3; i++) { tabs.ColumnDefinitions.Add(new ColumnDefinition()); pageHost.Children.Add(pages[i]); pages[i].Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed; }
        void SelectPage(int index)
        {
            for (var i = 0; i < 3; i++)
            {
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                tabButtons[i].Background = i == index ? Brushes.White : Brushes.Transparent;
                tabButtons[i].Foreground = i == index ? blue : muted;
                tabButtons[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        var names = new[] { "我的记忆", "云端同步", "备份与恢复" };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var button = MakeButton(names[i], "memory-nav-" + i, () => SelectPage(index));
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.BorderThickness = new Thickness(0); button.Margin = new Thickness(0); button.Padding = new Thickness(8, 8, 8, 8);
            Grid.SetColumn(button, i); tabs.Children.Add(button); tabButtons[i] = button;
        }
        panel.Children.Add(tabFrame); panel.Children.Add(pageHost);

        Guid? selected = null;
        var list = new ListBox { Height = 118, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var itemBorder = new FrameworkElementFactory(typeof(Border));
        itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); itemBorder.SetValue(Border.PaddingProperty, new Thickness(10, 9, 10, 9));
        itemBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent); itemBorder.Name = "Selection";
        itemBorder.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = itemBorder };
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Color("#EAF1FF"), "Selection")); template.Triggers.Add(selectedTrigger);
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Color("#F0F5FC"), "Selection")); template.Triggers.Add(hoverTrigger);
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template)); list.ItemContainerStyle = itemStyle;
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 24, 10, 0), IsHitTestVisible = false };
        var emptyTitle = Text("还没有记忆", 14, true); emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var emptyHint = Text("从一个称呼、一杯喜欢的茶开始。", 12); emptyHint.Margin = new Thickness(0, 7, 0, 0);
        empty.Children.Add(emptyTitle); empty.Children.Add(emptyHint);
        var listHost = new Grid(); listHost.Children.Add(list); listHost.Children.Add(empty);
        var count = Text("0 条", 12); count.Foreground = blue;
        var libraryBody = new StackPanel(); libraryBody.Children.Add(listHost);
        var library = Card("已经记住的事", libraryBody);
        var libraryStack = (StackPanel)library.Child;
        var libraryHeader = new DockPanel();
        var newButton = MakeButton("＋ 新记忆", "memory-new", () => clear()); newButton.Margin = new Thickness(8, -6, 0, -6); newButton.Padding = new Thickness(10, 6, 10, 6);
        DockPanel.SetDock(newButton, Dock.Right); libraryHeader.Children.Add(newButton);
        DockPanel.SetDock(count, Dock.Right); count.Margin = new Thickness(8, 2, 0, 0); libraryHeader.Children.Add(count);
        libraryHeader.Children.Add(Text("已经记住的事", 14, true)); libraryStack.Children.RemoveAt(0); libraryStack.Children.Insert(0, libraryHeader);
        pages[0].Children.Add(library);

        var editorTitle = Text("添一条小记忆", 14, true);
        var editor = new TextBox { AcceptsReturn = true, MinHeight = 66, MaxHeight = 120, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(editor, "memory-editor");
        var localOnly = new CheckBox { Content = "仅本机保存", FontSize = 16, Margin = new Thickness(0, 0, 10, 0) };
        var allowCloud = new CheckBox { Content = "允许云端模型使用", FontSize = 16 };
        localOnly.Click += (_, _) => { allowCloud.IsEnabled = localOnly.IsChecked != true; if (!allowCloud.IsEnabled) allowCloud.IsChecked = false; };
        var permissions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 }; permissions.Children.Add(localOnly); permissions.Children.Add(allowCloud);
        var privacy = new Expander { Header = "保存范围与使用权限", FontSize = 16, Foreground = muted, Margin = new Thickness(0, 9, 0, 10), Content = permissions };
        var editBody = new StackPanel(); editBody.Children.Add(Input(editor, "例如：我喜欢喝茶，叫我小黎就好。")); editBody.Children.Add(privacy);
        var editButtons = new WrapPanel();
        var add = MakeButton("记下", "memory-save", () =>
        {
            if (!preferences.MemoryEnabled) { Show("先开启上方的记忆开关吧。"); return; }
            EnsureCompanionMemory().Remember(editor.Text, localOnly.IsChecked == true, allowCloud.IsChecked == true);
            clear(); refresh(); Show("记住啦，下次鱼鱼会想起来的。");
        }, true);
        var update = MakeButton("保存修改", "memory-edit", () =>
        {
            if (selected is not Guid id) return;
            EnsureCompanionMemory().Update(id, editor.Text, localOnly.IsChecked == true, allowCloud.IsChecked == true);
            refresh(); Show("这条记忆已经更新。");
        }, true);
        var delete = MakeButton("删除", "memory-delete", () =>
        {
            if (selected is not Guid id) return;
            EnsureCompanionMemory().Delete(id); clear(); refresh(); Show("这条记忆已删除。");
        }, danger: true);
        editButtons.Children.Add(add); editButtons.Children.Add(update); editButtons.Children.Add(delete);
        editButtons.Children.Add(MakeButton("和鱼鱼聊聊", "memory-chat", OpenFishChat)); editBody.Children.Add(editButtons);
        var editCard = Card("", editBody); var editStack = (StackPanel)editCard.Child;
        editStack.Children.RemoveAt(0); editStack.Children.Insert(0, editorTitle); pages[0].Children.Add(editCard);
        var conflictText = Text("", 12);
        var conflictBody = new StackPanel(); conflictBody.Children.Add(conflictText);
        var conflictButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Resolve(bool cloud)
        {
            if (selected is not Guid id) return;
            EnsureCompanionMemory().ResolveConflict(id, cloud); refresh(); Show("已保留你选择的文字，等待同步确认。");
        }
        conflictButtons.Children.Add(MakeButton("保留本地", "memory-keep-local", () => Resolve(false)));
        conflictButtons.Children.Add(MakeButton("采用云端", "memory-use-cloud", () => Resolve(true)));
        conflictBody.Children.Add(conflictButtons);
        var conflictCard = Card("这条记忆有两个版本", conflictBody); conflictCard.Background = Color("#FFF9EF"); conflictCard.Visibility = Visibility.Collapsed;
        pages[0].Children.Add(conflictCard);

        var syncBody = new StackPanel();
        syncBody.Children.Add(Toggle("自动同步记忆", preferences.SyncEnabled, value => { preferences.SyncEnabled = value; EnsureCompanionMemory().SyncEnabled = value; Show(value ? "自动同步已开启。" : "自动同步已暂停，本机记忆仍会保留。"); }));
        var server = new TextBox { Text = preferences.SyncServerUrl ?? "" };
        AutomationProperties.SetAutomationId(server, "memory-server");
        syncBody.Children.Add(Input(server, "https://你的同步服务器"));
        var connectionActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void SaveServer()
        {
            preferences.SyncServerUrl = server.Text.Trim(); SaveCompanion();
            if (companionMemory != null) companionMemory.ServerUrl = preferences.SyncServerUrl;
        }
        connectionActions.Children.Add(AsyncButton("立即同步", "memory-sync-now", async () => { SaveServer(); Show(await EnsureCompanionMemory().SyncNow()); }, true));
        connectionActions.Children.Add(MakeButton("保存地址", "memory-save-server", () => { SaveServer(); Show("同步地址已保存。"); })); syncBody.Children.Add(connectionActions);
        pages[1].Children.Add(Card("云端记忆", syncBody, "在不同设备间延续同一份记忆，正文先在本机加密。"));

        var pairingBody = new StackPanel();
        var pairing = new TextBox(); AutomationProperties.SetAutomationId(pairing, "memory-pairing-code");
        pairingBody.Children.Add(Input(pairing, "输入服务器管理员提供的配对码"));
        var pairingActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        pairingActions.Children.Add(AsyncButton("配对这台设备", "memory-pair-device", async () => { SaveServer(); Show(await EnsureCompanionMemory().Pair(pairing.Text, Environment.MachineName)); pairing.Text = ""; }, true));
        pairingActions.Children.Add(MakeButton("创建新档案", "memory-create-archive", () => { SaveServer(); EnsureCompanionMemory().CreateArchive(); Show(companionMemory!.Status); }));
        pairingBody.Children.Add(pairingActions);
        var firstDeviceNote = Text("第一次使用：配对后创建新档案。已有档案时，通过下方传递口令加入。", 12); firstDeviceNote.Margin = new Thickness(0, 10, 0, 0); pairingBody.Children.Add(firstDeviceNote);
        pages[1].Children.Add(Card("接入这台设备", pairingBody));
        var transfer = new TextBox(); AutomationProperties.SetAutomationId(transfer, "memory-transfer-code");
        var transferBody = new StackPanel();
        transferBody.Children.Add(Text("在两台设备上输入同一段口令，至少 16 个字符。", 12));
        var transferInput = Input(transfer, "一段足够长、仅用于这次传递的口令"); transferInput.Margin = new Thickness(0, 9, 0, 10); transferBody.Children.Add(transferInput);
        var transferActions = new WrapPanel();
        transferActions.Children.Add(AsyncButton("从这里分享", "memory-publish-transfer", async () => { SaveServer(); await EnsureCompanionMemory().PublishEnvelope(transfer.Text); Show(companionMemory!.Status); }));
        transferActions.Children.Add(AsyncButton("加入已有档案", "memory-redeem-transfer", async () => { SaveServer(); await EnsureCompanionMemory().RedeemEnvelope(transfer.Text); transfer.Text = ""; Show(companionMemory!.Status); }));
        transferBody.Children.Add(transferActions);
        pages[1].Children.Add(new Expander { Header = "连接另一台电脑", Content = transferBody, FontSize = 17, Foreground = ink, Margin = new Thickness(4, 0, 4, 8), Padding = new Thickness(0, 10, 0, 0) });

        var recoveryBody = new StackPanel();
        var recoveryActions = new WrapPanel();
        recoveryActions.Children.Add(MakeButton("导出恢复包", "memory-export", () =>
        {
            SaveServer();
            var memory = EnsureCompanionMemory();
            var json = memory.ExportRecovery();
            var dialog = new SaveFileDialog { Filter = "恢复包|*.json", FileName = "bigbluefish-recovery.json" };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, json); Show("恢复包已导出，请单独保管。");
        }, true));
        recoveryActions.Children.Add(MakeButton("导入恢复包", "memory-import", () =>
        {
            var dialog = new OpenFileDialog { Filter = "恢复包|*.json" };
            if (dialog.ShowDialog() != true) return;
            var memory = EnsureCompanionMemory(); memory.ImportRecovery(File.ReadAllText(dialog.FileName));
            preferences.SyncServerUrl = memory.ServerUrl; server.Text = memory.ServerUrl ?? ""; SaveCompanion(); refresh(); Show(memory.Status);
        }));
        recoveryBody.Children.Add(recoveryActions);
        pages[2].Children.Add(Card("给记忆留一把备用钥匙", recoveryBody, "恢复包包含档案密钥。换电脑时可以用它找回记忆，请保存在私人位置，不要发到聊天里。"));
        var resetBody = new StackPanel();
        resetBody.Children.Add(Text("仅在服务器已清空记忆、并提示版本重置时使用。仅本机保存的记忆会保留。", 12));
        var reset = MakeButton("接受云端重置", "memory-accept-reset", () =>
        {
            if (MessageBox.Show("接受后将清除本机的共享记忆和待同步修改。请先导出恢复包。继续吗？", "接受云端重置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            EnsureCompanionMemory().AcceptCloudReset(); refresh(); Show(companionMemory!.Status);
        }, danger: true);
        reset.Margin = new Thickness(0, 10, 0, 0); resetBody.Children.Add(reset);
        pages[2].Children.Add(new Expander { Header = "高级操作", Foreground = muted, FontSize = 17, Content = resetBody, Margin = new Thickness(4, 2, 4, 0) });
        panel.Children.Add(statusFrame);

        void ResetEditor()
        {
            selected = null; list.SelectedIndex = -1; editor.Text = "";
            localOnly.IsChecked = false; allowCloud.IsChecked = false; allowCloud.IsEnabled = true;
            editorTitle.Text = "添一条小记忆"; add.Visibility = Visibility.Visible; update.Visibility = delete.Visibility = Visibility.Collapsed;
            conflictCard.Visibility = Visibility.Collapsed;
        }
        void LoadSelection()
        {
            if (list.SelectedItem is not ListBoxItem row || row.Tag is not Guid id || companionMemory == null) return;
            var record = companionMemory.List().FirstOrDefault(item => item.Id == id);
            if (record == null) return;
            selected = id; editor.Text = record.Content; editorTitle.Text = "编辑这条记忆";
            localOnly.IsChecked = record.Scope == SyncScope.LocalOnly; allowCloud.IsChecked = record.AllowCloudModel; allowCloud.IsEnabled = !localOnly.IsChecked.Value;
            add.Visibility = Visibility.Collapsed; update.Visibility = delete.Visibility = Visibility.Visible;
            var conflict = companionMemory.Conflicts().FirstOrDefault(item => item.RecordId == id);
            conflictCard.Visibility = conflict == null ? Visibility.Collapsed : Visibility.Visible;
            if (conflict != null) conflictText.Text = "本地：" + record.Content + "\n\n云端：" + conflict.RemoteContent;
        }
        void Reload()
        {
            var selection = selected;
            list.Items.Clear();
            var records = preferences.MemoryEnabled && companionMemory != null ? companionMemory.List() : Array.Empty<MemoryRecord>();
            count.Text = records.Count + " 条"; empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var record in records)
            {
                var row = new DockPanel();
                var tag = Text(record.Scope == SyncScope.LocalOnly ? "本机" : "共享", 11); tag.Foreground = record.Scope == SyncScope.LocalOnly ? muted : blue; tag.Margin = new Thickness(12, 2, 0, 0);
                DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag);
                var content = Text(record.Content, 13); content.Foreground = ink; content.TextWrapping = TextWrapping.NoWrap; content.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(content);
                list.Items.Add(new ListBoxItem { Content = row, Tag = record.Id });
            }
            if (selection is Guid old)
            {
                var matching = list.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag is Guid id && id == old);
                if (matching != null) { list.SelectedItem = matching; LoadSelection(); }
                else clear();
            }
            reset.IsEnabled = companionMemory?.PendingCloudEpoch != null; reset.Opacity = reset.IsEnabled ? 1 : 0.45;
        }
        list.SelectionChanged += (_, _) => LoadSelection();
        refreshMemoryList = Reload;
        refresh = Reload; clear = ResetEditor;
        clear(); SelectPage(0);
        try { if (preferences.MemoryEnabled) EnsureCompanionMemory(); refresh(); }
        catch (Exception error) { Show(error.Message); }
    }
}
