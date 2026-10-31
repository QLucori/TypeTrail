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
    // One controller owns the state; this file contains its settings responsibilities.
    internal sealed partial class Controller {
        void ChangeLanguage(string language) {
            settings.Language = language; L.Language = language; captureStatus = L.Rewrite(captureStatus);
            if (window != null) {
                L.Apply(window);
                UpdateThemeButton(); UpdateLanguageButton();
                foreach (ComboBoxItem choice in Find<ComboBox>("LanguageCombo").Items) if ((string)choice.Tag == language) Find<ComboBox>("LanguageCombo").SelectedItem = choice;
                Find<TextBlock>("EmptyShortcut").Text = HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey) + L.T("    随时呼出");
            }
            var items = tray.ContextMenuStrip.Items;
            items[0].Text = L.T("打开输入历史"); items[2].Text = L.T("设置"); items[4].Text = L.T("退出拾字");
            tray.Text = L.T("拾字 · ") + HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey);
            disk.SaveSettings(settings);if(disk.Error!=null)Notify(disk.Error);RefreshList(); UpdatePause(); UpdateUsage();
        }
        void TogglePause() {
            if (migrationBlocked) { Notify(L.T("旧历史尚未完成备份，请检查数据目录后重新打开拾字")); return; }
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=false;
            if(!settings.Paused) {
                FlushCommittedInput();nativeCapture.Dispose();nativeCapture.Drain(ReceiveCommitted);DrainAdmitted(true);
            } else {nativeCapture.Dispose();nativeCapture.Drain(delegate(CommittedInput input,string sourceName) {});}
            settings.Paused = !settings.Paused; epoch++; lastInput = 0; history.ResetSession();
            pendingPaste=null; pendingPasteSerial=-1; pasteHints.Reset();
            nextPasteBaseline=null;assemblingPasteBaseline=null;pendingPasteBaseline=null;pendingPasteConsumed=0;
            directHistory.Reset(); typing.Clear();
            ResetFieldAdmission();
            if(keyboardMonitor!=null)keyboardMonitor.Clear();
            if(!settings.Paused)PrepareCapture();
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=!settings.Paused;
            retiredTyping.Clear(); protectedWindow=IntPtr.Zero;
            if (settings.Paused) StopWorker(); else StartWorker();
            disk.SaveSettings(settings); UpdatePause(); UpdateUsage();
        }
        void UpdatePause() {
            if (pauseMenu != null) pauseMenu.Text = settings.Paused ? L.T("恢复记录") : L.T("暂停记录");
            if (window == null) return;
            var button = Find<Button>("PauseButton");
            bool warning = captureState == "partial" || captureState == "capture_error" || captureState == "unsupported_input" || captureState == "office_unavailable" || captureState == "unavailable" || captureState == "uncertain_input";
            string label = captureState == "partial" ? L.T("记录不完整") : captureState == "capture_error" ? L.T("监听受限") : warning ? L.T("当前未记录") : captureState == "protected" || captureState == "excluded" ? L.T("已跳过") : captureState == "composing" ? L.T("等待选词") : captureState == "ok" ? L.T("正在记录") : L.T("等待输入");
            button.ToolTip = L.Rewrite(nativeCapture.Warning ?? captureStatus);
            button.Content = settings.Paused ? L.T("Ⅱ  已暂停") : "●  " + label;
            button.SetResourceReference(Button.BackgroundProperty, settings.Paused || warning ? "ThemeWarning" : "ThemePositive");
            button.SetResourceReference(Button.ForegroundProperty, settings.Paused || warning ? "ThemeWarningText" : "ThemePositiveText");
            displayedCaptureState=captureState;displayedCaptureWarning=nativeCapture.Warning;
        }
        void UpdateThemeButton() {
            if (window == null) return;
            Find<System.Windows.Shapes.Path>("ThemeSun").Visibility = settings.DarkMode ? Visibility.Visible : Visibility.Collapsed;
            Find<System.Windows.Shapes.Path>("ThemeMoon").Visibility = settings.DarkMode ? Visibility.Collapsed : Visibility.Visible;
            var button = Find<Button>("ThemeButton");
            button.ToolTip = L.T(settings.DarkMode ? "切换到浅色模式" : "切换到深色模式");
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        }
        void UpdateLanguageButton() {
            if (window == null) return;
            var button = Find<Button>("LanguageButton");
            button.ToolTip = L.T("界面语言");
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            if (button.ContextMenu != null) foreach (MenuItem choice in button.ContextMenu.Items) choice.IsChecked = (string)choice.Tag == settings.Language;
        }
        void OpenSettings() {
            recordingHotkey = false; candidateModifiers = settings.HotkeyModifiers; candidateKey = settings.HotkeyKey;
            Find<Button>("HotkeyButton").Content = HotkeyLabel(candidateModifiers, candidateKey);
            var combo = Find<ComboBox>("LimitCombo");
            foreach (ComboBoxItem item in combo.Items) if (Int32.Parse((string)item.Tag) == settings.CharacterLimit) combo.SelectedItem = item;
            if (combo.SelectedItem == null) combo.SelectedIndex = 1;
            Find<CheckBox>("RememberCheck").IsChecked = settings.RememberHistory;
            Find<CheckBox>("StartupCheck").IsChecked = File.Exists(StartupPath());
            Find<TextBox>("ExcludedBox").Text = settings.ExcludedApps;
            foreach (ComboBoxItem language in Find<ComboBox>("LanguageCombo").Items) if ((string)language.Tag == settings.Language) Find<ComboBox>("LanguageCombo").SelectedItem = language;
            Find<TextBlock>("SettingsStatus").Text = ""; Find<Button>("ClearButton").Content = L.T("清空所有历史");
            Find<Border>("SettingsOverlay").Visibility = Visibility.Visible;
        }
        void CloseSettings() { recordingHotkey = false; Find<Border>("SettingsOverlay").Visibility = Visibility.Collapsed; }
        void SaveSettings() {
            if (recordingHotkey) { Find<TextBlock>("SettingsStatus").Text = L.T("请先完成快捷键设置，或按 Esc 取消。"); return; }
            if (liveCapture) Native.UnregisterHotKey(source.Handle, 1);
            if (liveCapture && !Native.RegisterHotKey(source.Handle, 1, candidateModifiers | 0x4000, candidateKey)) {
                Native.RegisterHotKey(source.Handle, 1, settings.HotkeyModifiers | 0x4000, settings.HotkeyKey);
                Find<TextBlock>("SettingsStatus").Text = L.T("这个快捷键已被占用，请换一个。原快捷键仍保留。"); return;
            }
            settings.HotkeyModifiers = candidateModifiers; settings.HotkeyKey = candidateKey;
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=false;
            DrainKeyboardInput();
            settings.CharacterLimit = Int32.Parse((string)((ComboBoxItem)Find<ComboBox>("LimitCombo").SelectedItem).Tag);
            settings.ExcludedApps = Find<TextBox>("ExcludedBox").Text;
            settings.RememberHistory = Find<CheckBox>("RememberCheck").IsChecked == true;
            nativeCapture.Dispose();nativeCapture.Drain(ReceiveCommitted);DrainAdmitted(true);directHistory.Reset();
            ResetFieldAdmission();
            if(!settings.Paused)PrepareCapture();
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=!settings.Paused;
            pendingPaste=null; pendingPasteSerial=-1; pasteHints.Reset();
            nextPasteBaseline=null;assemblingPasteBaseline=null;pendingPasteBaseline=null;pendingPasteConsumed=0;
            history.Limit = settings.CharacterLimit; history.Trim(); history.ResetSession(); epoch++;
            typing.Clear(); retiredTyping.Clear(); protectedWindow=IntPtr.Zero;
            try {
                if (!settings.RememberHistory) disk.DeleteHistory();
                SetStartup(Find<CheckBox>("StartupCheck").IsChecked == true);
            } catch { Find<TextBlock>("SettingsStatus").Text = L.T("部分设置写入失败，请检查目录权限后重试。"); return; }
            disk.SaveSettings(settings); dirty = true; SaveHistory();
            if(disk.Error!=null){Find<TextBlock>("SettingsStatus").Text=disk.Error;return;}
            tray.Text = L.T("拾字 · ") + HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey);
            Find<TextBlock>("EmptyShortcut").Text = HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey) + L.T("    随时呼出");
            CloseSettings(); RefreshList(); Notify(L.T("设置已保存"));
        }
        public static string HotkeyLabel(uint mods, uint key) {
            var labels = new List<string>();
            if ((mods & 2) != 0) labels.Add("Ctrl"); if ((mods & 1) != 0) labels.Add("Alt");
            if ((mods & 4) != 0) labels.Add("Shift"); if ((mods & 8) != 0) labels.Add("Win");
            labels.Add(key == 192 ? "`" : key == 32 ? "Space" : KeyInterop.KeyFromVirtualKey((int)key).ToString());
            return String.Join(" + ", labels);
        }
    }
}
