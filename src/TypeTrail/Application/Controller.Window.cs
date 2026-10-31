using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeTrail {
    // One controller owns the state; this file contains its window responsibilities.
    internal sealed partial class Controller {
        T Find<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        static Brush Color(string color) { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color)); }
        void EnsureWindow() {
            if (window != null) return;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TypeTrail.Window.xaml")) window = (Window)XamlReader.Load(s);
            L.Apply(window);
            Theme.Apply(window, settings.DarkMode);
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("TypeTrail.Icon.png")) {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = icon; bitmap.EndInit(); bitmap.Freeze();
                Find<System.Windows.Controls.Image>("BrandImage").Source = bitmap; window.Icon = bitmap;
            }
            new WindowInteropHelper(window).EnsureHandle();
            BindEvents(); UpdateThemeButton(); UpdateLanguageButton(); UpdatePause();
        }
        void BindEvents() {
            Find<Button>("CloseButton").Click += delegate { Hide(); };
            Find<Grid>("DragArea").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (!(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase)) { try { window.DragMove(); } catch {} }
            };
            Find<Button>("PauseButton").Click += delegate { TogglePause(); };
            Find<Button>("SettingsButton").Click += delegate { OpenSettings(); };
            var languageButton = Find<Button>("LanguageButton");
            var languageMenu = new ContextMenu {
                Style = (Style)window.Resources["LanguageMenu"],
                PlacementTarget = languageButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                HorizontalOffset = 0, VerticalOffset = 3
            };
            languageMenu.Resources.MergedDictionaries.Add(window.Resources);
            string[] languageCodes = {"auto", "zh", "en"};
            string[] languageLabels = {"跟随系统 / System", "简体中文", "English"};
            for (int index = 0; index < languageCodes.Length; index++) {
                var choice = new MenuItem {Header=languageLabels[index], Tag=languageCodes[index], IsCheckable=true, Style=(Style)window.Resources["LanguageMenuItem"]};
                choice.Click += delegate(object sender, RoutedEventArgs e) { ChangeLanguage((string)((MenuItem)sender).Tag); };
                languageMenu.Items.Add(choice);
            }
            languageButton.ContextMenu = languageMenu;
            languageButton.Click += delegate { UpdateLanguageButton(); languageMenu.IsOpen = !languageMenu.IsOpen; };
            Find<Button>("ThemeButton").Click += delegate {
                settings.DarkMode = !settings.DarkMode;
                Theme.Apply(window, settings.DarkMode); Theme.ApplyTray(tray.ContextMenuStrip, settings.DarkMode);
                UpdateThemeButton(); UpdatePause(); disk.SaveSettings(settings);if(disk.Error!=null)Notify(disk.Error);
            };
            Find<Button>("SettingsCloseButton").Click += delegate { CloseSettings(); };
            Find<Button>("SaveSettingsButton").Click += delegate { SaveSettings(); };
            Find<Button>("ClearButton").Click += delegate { ClearHistory(); };
            Find<ComboBox>("LanguageCombo").SelectionChanged += delegate {
                var item = Find<ComboBox>("LanguageCombo").SelectedItem as ComboBoxItem;
                if (item == null || (string)item.Tag == settings.Language) return;
                ChangeLanguage((string)item.Tag);
            };
            Find<Button>("CopyButton").Click += delegate { Copy(false); };
            Find<Button>("CopySelectionButton").Click += delegate { Copy(true); };
            Find<Button>("DeleteButton").Click += delegate { DeleteSelected(); };
            Find<TextBox>("SearchBox").TextChanged += delegate {
                Find<TextBlock>("SearchHint").Visibility = String.IsNullOrEmpty(Find<TextBox>("SearchBox").Text) ? Visibility.Visible : Visibility.Collapsed;
                RefreshList();
            };
            Find<ListBox>("HistoryList").SelectionChanged += delegate { if (!refreshing) RefreshDetail(); };
            Find<ComboBox>("VersionCombo").SelectionChanged += delegate { if (!refreshingVersions) RefreshVersion(); };
            Find<TextBox>("DetailText").SelectionChanged += delegate {
                int count = Find<TextBox>("DetailText").SelectionLength;
                Find<Button>("CopySelectionButton").IsEnabled = count > 0;
                Find<TextBlock>("SelectionHint").Text = count > 0 ? L.T("已选中 ") + count + L.T(" 个字符") : L.T("拖选文字，可只复制选中部分");
            };
            Find<Button>("HotkeyButton").Click += delegate { recordingHotkey = true; Find<Button>("HotkeyButton").Content = L.T("请按组合键…"); };
            window.PreviewKeyDown += OnKey;
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!disposed && !closingPanel) { e.Cancel = true; ui.BeginInvoke(new Action(Hide)); } };
        }
        void RefreshCaptureIndicator() {
            if(window==null)return;
            string warning=nativeCapture.Warning;
            if(displayedCaptureState==captureState && displayedCaptureStatus==captureStatus && displayedCaptureWarning==warning)return;
            UpdatePause();UpdateUsage();
        }
        public void Show() {
            if (disposed || popupPending) return;
            bool recent = lastInput != 0 && (Stopwatch.GetTimestamp() - lastInput) / (double)Stopwatch.Frequency < 30;
            if (!settings.Paused && recent && !(window != null && window.IsActive)) {
                // Keep the original editor focused until its latest confirmed text is read.
                popupPending = true; popupFlushId = 0; popupDeadline = DateTime.UtcNow.AddMilliseconds(850);
                if (!pending) SendCapture(true);
                return;
            }
            ShowCore();
        }
        void ShowCore() {
            if (disposed) return;
            FlushCommittedInput();
            panelOpenedAt=DateTime.UtcNow;
            IntPtr foreground = Native.GetForegroundWindow();
            EnsureWindow();
            IntPtr panelHandle = new WindowInteropHelper(window).Handle;
            if (foreground != panelHandle) previousWindow = foreground;
            Rect area = SystemParameters.WorkArea;
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            var transform = HwndSource.FromHwnd(panelHandle).CompositionTarget.TransformFromDevice;
            Point top = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            Point bottom = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            area = new Rect(top, bottom);
            window.Width = Math.Min(880, area.Width - 24); window.Height = Math.Min(590, area.Height - 24);
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
            Find<Border>("SettingsOverlay").Visibility = Visibility.Collapsed;
            RefreshList(); UpdateUsage();
            if(firstShownRecords<0){firstShownRecords=Find<ListBox>("HistoryList").Items.Count;firstShownFilter=Find<TextBox>("SearchBox").Text.Trim().Length>0;}
            Find<TextBlock>("EmptyShortcut").Text = HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey) + L.T("    随时呼出");
            window.ShowInTaskbar = true;
            window.Show(); Native.ShowWindow(panelHandle, 5); window.Activate(); Native.SetForegroundWindow(panelHandle);
            Find<TextBox>("SearchBox").Focus();
            listDirty = false;
            if (settingsAfterPopup) { settingsAfterPopup = false; OpenSettings(); }
            if (pendingNotice != null) { string notice = pendingNotice; pendingNotice = null; Notify(notice); }
            WriteHistoryState(true);
        }
        void Hide() {
            popupPending = false;
            if (window == null) return;
            FlushCommittedInput();
            recordingHotkey = false; toastTimer.Stop(); window.Hide();
            closingPanel = true; window.Close(); window = null; closingPanel = false;
            if (previousWindow != IntPtr.Zero && Native.IsWindow(previousWindow)) Native.SetForegroundWindow(previousWindow);
            if (dirty) SaveHistory();
            WriteHistoryState(true);
            ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { GC.Collect(); }));
        }
        void RefreshList() {
            if (window == null) return;
            string query = Find<TextBox>("SearchBox").Text.Trim();
            var list = Find<ListBox>("HistoryList");
            var old = list.SelectedItem as Entry;
            var filtered = history.Entries.Where(item => query.Length == 0 || item.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || item.Source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || item.Versions.Any(v => v.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if(disk.HistoryReadPending)filtered.Clear();
            refreshing = true; list.ItemsSource = filtered;
            list.SelectedItem = old != null && filtered.Contains(old) ? old : filtered.FirstOrDefault(); refreshing = false;
            Find<TextBlock>("ListHeading").Text = disk.HistoryReadPending?L.T("正在恢复历史…"):(query.Length == 0 ? L.T("最近输入") : L.T("搜索结果")) + "  /  " + filtered.Count;
            bool empty = filtered.Count == 0;
            Find<Border>("EmptyPanel").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("EmptyTitle").Text = history.Entries.Count == 0 ? L.T("下一次灵感，不用重打一遍") : L.T("没有找到这段文字");
            Find<TextBlock>("EmptyDescription").Text = history.Entries.Count == 0 ? L.T("去任意支持的输入框打字，历史会自动出现在这里。") : L.T("换个关键词，或清空搜索查看全部记录。");
            if(disk.HistoryReadPending) {
                Find<TextBlock>("EmptyTitle").Text=L.T("正在恢复历史…");
                Find<TextBlock>("EmptyDescription").Text=L.T("原历史文件已保护，新输入会暂存在内存，恢复后一起显示。");
            }
            RefreshDetail(); UpdateUsage();
            WriteHistoryState();
        }
        void RefreshDetail() {
            Entry entry = Find<ListBox>("HistoryList").SelectedItem as Entry;
            Find<Grid>("DetailPanel").Visibility = entry == null ? Visibility.Hidden : Visibility.Visible;
            if (entry == null) return;
            Find<TextBlock>("DetailSource").Text = L.Display(entry.Source);
            var combo = Find<ComboBox>("VersionCombo");
            var oldChoice = combo.SelectedItem as ComboBoxItem;
            var oldRevision = oldChoice == null ? null : oldChoice.Tag as Revision;
            refreshingVersions = true;
            combo.Items.Clear(); combo.Items.Add(new ComboBoxItem { Content = entry.PreviousVersion ? L.T("修改前 · ")+entry.Updated.ToString("HH:mm:ss") : L.T(entry.IsCleared ? "当前内容 · 已清空" : "当前内容") });
            var versions = entry.GetDisplayVersions();
            foreach (var revision in versions) combo.Items.Add(new ComboBoxItem { Content = L.T("修改前 · ") + revision.Saved.ToString("HH:mm:ss"), Tag = revision });
            combo.Visibility = versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            combo.SelectedIndex = oldRevision != null && versions.Contains(oldRevision) ? versions.IndexOf(oldRevision) + 1 : 0;
            string query = Find<TextBox>("SearchBox").Text.Trim();
            if (query.Length > 0 && entry.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) {
                int matched = versions.FindIndex(v => v.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                if (matched >= 0) combo.SelectedIndex = matched + 1;
            }
            refreshingVersions = false;
            RefreshVersion();
        }
        void RefreshVersion() {
            Entry entry = Find<ListBox>("HistoryList").SelectedItem as Entry;
            if (entry == null) return;
            var choice = Find<ComboBox>("VersionCombo").SelectedItem as ComboBoxItem;
            var revision = choice == null ? null : choice.Tag as Revision;
            string displayed = (revision == null ? entry.Text : revision.Text) ?? "";
            DateTime time = revision == null ? entry.Updated : revision.Saved;
            Find<TextBlock>("DetailMeta").Text = time.ToString(L.T("MM月dd日  HH:mm:ss"), System.Globalization.CultureInfo.GetCultureInfo(L.English ? "en-US" : "zh-CN")) + "   ·   " + displayed.Length.ToString("N0") + L.T(" 字符") + (revision != null || entry.PreviousVersion ? L.T("   ·   修改前") : "") + (entry.Truncated ? L.T("   ·   部分内容") : "");
            var text = Find<TextBox>("DetailText");
            if (text.Text != displayed) { text.Text = displayed; text.ScrollToHome(); }
            bool cleared = revision == null && entry.IsCleared;
            Find<StackPanel>("ClearedDraftPanel").Visibility = cleared ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("CopyButton").IsEnabled = displayed.Length > 0;
        }
        void UpdateUsage() {
            if (window == null) return;
            string status = L.Rewrite(captureStatus);
            int sourceEnd = status.IndexOf(" · ", StringComparison.Ordinal);
            if (sourceEnd >= 0) status = L.Display(status.Substring(0, sourceEnd)) + status.Substring(sourceEnd);
            Find<TextBlock>("StatusText").Text = settings.Paused ? L.T("记录已暂停 · 历史仍可查看") : status;
            Find<TextBlock>("UsageText").Text = history.Characters.ToString("N0") + " / " + settings.CharacterLimit.ToString("N0") + L.T(" 字符");
            displayedCaptureStatus=captureStatus;
        }
        void Copy(bool selected) {
            string text = selected ? Find<TextBox>("DetailText").SelectedText : Find<TextBox>("DetailText").Text;
            if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetDataObject(text, true); Notify(selected ? L.T("已复制选中的文字") : L.T("已复制整段文字 · Esc 返回原应用")); }
            catch { Notify(L.T("剪贴板正忙，请再点一次复制。")); }
        }
        public void Notify(string text) {
            if (disposed) return;
            if (window == null) { pendingNotice = text; return; }
            Find<TextBlock>("ToastText").Text = text; Find<Border>("Toast").Visibility = Visibility.Visible;
            if (toastTimer != null) { toastTimer.Stop(); toastTimer.Start(); }
        }
        void OnKey(object sender, KeyEventArgs e) {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (recordingHotkey) {
                e.Handled = true;
                if (key == Key.Escape) { recordingHotkey = false; Find<Button>("HotkeyButton").Content = HotkeyLabel(candidateModifiers, candidateKey); return; }
                if (key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;
                ModifierKeys mods = Keyboard.Modifiers;
                if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) { Find<TextBlock>("SettingsStatus").Text = L.T("请搭配 Ctrl、Alt 或 Win，避免影响正常打字。"); return; }
                candidateModifiers = ((mods & ModifierKeys.Alt) != 0 ? 1u : 0u) | ((mods & ModifierKeys.Control) != 0 ? 2u : 0u) | ((mods & ModifierKeys.Shift) != 0 ? 4u : 0u) | ((mods & ModifierKeys.Windows) != 0 ? 8u : 0u);
                candidateKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                Find<Button>("HotkeyButton").Content = HotkeyLabel(candidateModifiers, candidateKey); recordingHotkey = false;
                Find<TextBlock>("SettingsStatus").Text = L.T("保存时会检查快捷键是否被占用。"); return;
            }
            if (key == Key.Escape) { if (Find<Border>("SettingsOverlay").Visibility == Visibility.Visible) CloseSettings(); else Hide(); e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F) { Find<TextBox>("SearchBox").Focus(); Find<TextBox>("SearchBox").SelectAll(); e.Handled = true; }
            if (Find<Border>("SettingsOverlay").Visibility == Visibility.Visible) return;
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Copy(Find<TextBox>("DetailText").SelectionLength > 0); e.Handled = true; }
            if (key == Key.Down && Find<TextBox>("SearchBox").IsFocused) { Find<ListBox>("HistoryList").Focus(); if (Find<ListBox>("HistoryList").SelectedIndex < 0) Find<ListBox>("HistoryList").SelectedIndex = 0; e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.Z && deleted != null && !Find<TextBox>("SearchBox").IsFocused) {
                history.Entries.Insert(0, deleted); deleted = null; history.Trim(); dirty = true; RefreshList(); Notify(L.T("已恢复删除的记录")); e.Handled = true;
            }
        }
        static Drawing.Icon MakeIcon() {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TypeTrail.Icon.ico"))
            using (var icon = new Drawing.Icon(stream, 32, 32)) return (Drawing.Icon)icon.Clone();
        }
    }
}
