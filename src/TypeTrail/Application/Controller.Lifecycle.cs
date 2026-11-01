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
    // One controller owns the state; this file contains its lifecycle responsibilities.
    internal sealed partial class Controller {
        public Controller(string folder, string eventKey, bool show, bool background, bool captureNative=true) {
            liveCapture=captureNative;
            ui = Dispatcher.CurrentDispatcher;
            exe = Assembly.GetExecutingAssembly().Location; pid = Process.GetCurrentProcess().Id;
            disk = new DiskStore(folder); settings = disk.LoadSettings();
            fieldAdmission.Trace=Trace;
            int langIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--lang");
            if (langIndex >= 0 && Environment.GetCommandLineArgs().Length > langIndex + 1) {
                string language = Environment.GetCommandLineArgs()[langIndex + 1];
                if (language == "en" || language == "zh" || language == "auto") settings.Language = language;
            }
            L.Language = settings.Language;
            captureStatus = L.T("到输入框打字后，这里会显示记录状态");
            history = new History { Limit = settings.CharacterLimit };
            directHistory = new DirectInputHistory(history);
            var loaded=settings.RememberHistory?disk.LoadHistory():new List<Entry>();
            recordsAtLoad=loaded.Count;
            initialHistoryReadFailed=disk.HistoryReadPending;
            if(!disk.HistoryReadPending)history.Entries=ValidateLoadedHistory(loaded);
            history.Trim();
            // A tiny message-only window receives hotkeys; the full UI exists only while open.
            source = new HwndSource(new HwndSourceParameters("TypeTrail.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            source.AddHook(WindowMessage);
            if(!settings.Paused)PrepareCapture();
            keyboardMonitor = new KeyboardMonitor(delegate(KeyboardInput input) {
                return nativeCapture.PulseKeyboard(input.InputProcessId,input.Focus,input.Tick);
            },liveCapture);
            keyboardMonitor.Enabled=!settings.Paused;
            // Offline controller replays must never compete with the installed
            // app's hotkey or surface startup/repair windows on the user desktop.
            bool registered = !liveCapture || Native.RegisterHotKey(source.Handle, 1, settings.HotkeyModifiers | 0x4000, settings.HotkeyKey);
            tray = new Forms.NotifyIcon { Icon = MakeIcon(), Visible = liveCapture, Text = L.T("拾字 · ") + HotkeyLabel(settings.HotkeyModifiers, settings.HotkeyKey) };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(L.T("打开输入历史"), null, delegate { Show(); });
            pauseMenu = new Forms.ToolStripMenuItem(L.T("暂停记录"), null, delegate { TogglePause(); }); menu.Items.Add(pauseMenu);
            menu.Items.Add(L.T("设置"), null, delegate { settingsAfterPopup = true; Show(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(L.T("退出拾字"), null, delegate { Exit(); });
            tray.ContextMenuStrip = menu;
            Theme.ApplyTray(menu, settings.DarkMode);
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Show(); };
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventKey + "-show");
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventKey + "-exit");
            healthEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventKey + "-health");
            if(Environment.GetCommandLineArgs().Contains("--write-health"))BeginHealthReport();
            string[] languages = { "en", "zh", "auto" };
            for (int i = 0; i < languages.Length; i++) languageEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, eventKey + "-lang-" + languages[i]);
            var signalThread = new Thread(delegate() {
                while (!disposed) {
                    try {
                        int signal = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent, languageEvents[0], languageEvents[1], languageEvents[2], healthEvent }, 1000);
                        if (!disposed && signal == 0) ui.BeginInvoke(new Action(Show));
                        if (!disposed && signal == 1) ui.BeginInvoke(new Action(Exit));
                        if (!disposed && signal == 5) ui.BeginInvoke(new Action(BeginHealthReport));
                        if (!disposed && signal >= 2 && signal <= 4) { string requested = languages[signal - 2]; ui.BeginInvoke(new Action(delegate { ChangeLanguage(requested); })); }
                    } catch { break; }
                }
            }) { IsBackground = true }; signalThread.Start();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; toastTimer.Tick += delegate { if (window != null) Find<Border>("Toast").Visibility = Visibility.Collapsed; toastTimer.Stop(); };
            tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) }; tick.Tick += OnTick;
            StartWorker(); tick.Start();
            UpdatePause(); RefreshList(); UpdateUsage();
            bool first = settings.FirstRun; settings.FirstRun = false; disk.SaveSettings(settings);
            if (liveCapture && ((first && !background) || show || !registered)) Show();
            if (liveCapture && !registered) { OpenSettings(); Find<TextBlock>("SettingsStatus").Text = L.T("快捷键被其他程序占用，请设置一个新组合。"); }
            if (liveCapture && !keyboardMonitor.Running) Notify(L.T("输入活动检测未能启动，请退出后重新打开拾字。"));
            if (disk.Error != null) Notify(disk.Error);
            WriteHistoryState(true);
            ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { GC.Collect(); }));
            Microsoft.Win32.SystemEvents.SessionEnding += SessionEnding;
        }
        IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == 0x312 && wParam.ToInt32() == 1) {
                if (window != null && window.IsVisible && window.IsActive) Hide(); else Show();
                handled = true;
            }
            return IntPtr.Zero;
        }
        void OnTick(object sender, EventArgs e) {
            if (disposed) return;
            RetryHistoryLoad();
            DrainKeyboardInput();
            RecoverCaptureWorker();
            RefreshNativeCapture(false);
            if ((DateTime.UtcNow - nativeTraced).TotalSeconds > 3) { nativeTraced = DateTime.UtcNow; Trace("native targets=" + nativeCapture.Targets + " stats=" + String.Join(",", nativeCapture.Diagnostics) + " error=" + nativeCapture.Error); }
            nativeCapture.Drain(ReceiveCommitted);
            DrainAdmitted();
            ApplyNativeStatus();
            WriteHealthReport();
            if(window!=null && window.IsVisible)RefreshCaptureIndicator();
            DateTime now = DateTime.UtcNow;
            // Refresh a last, throttled update even when focus has moved to our panel.
            if (listDirty && window != null && window.IsVisible && (now - lastUi).TotalMilliseconds >= 100) { RefreshList(); listDirty = false; lastUi = now; }
            if (dirty && (now - lastSave).TotalSeconds >= 3) SaveHistory();
            if(exitPending) {
                if(now>=exitDeadline)FinishExit();
                else if(!pending && exitFlushId==0) { SendCapture(true);exitFlushId=requestId; }
                return;
            }
            if (popupPending) {
                if (now >= popupDeadline) { popupPending = false; ShowCore(); }
                else if (!pending && popupFlushId == 0) SendCapture(true);
                return;
            }
            if (settings.Paused) return;
            if (pending) {
                if ((now - requestedAt).TotalSeconds > 4) { StopWorker(); captureStatus = L.T("当前输入框响应较慢，已自动恢复采集服务"); StartWorker(); }
                return;
            }
            bool active = InputActive() || fieldAdmission.HasPending;
            bool settlingPanel=window!=null && window.IsVisible && (now-panelOpenedAt).TotalSeconds<3;
            if ((now - lastRequest).TotalMilliseconds < (active || focusDirty || settlingPanel ? 100 : 3000)) return;
            if (worker == null || worker.HasExited) { StartWorker(); return; }
            SendCapture(false);
        }
        void BeginHealthReport() { healthDeadline=DateTime.UtcNow.AddSeconds(30);nextHealthReport=DateTime.MinValue; }
        void WriteHealthReport() {
            DateTime now=DateTime.UtcNow;
            if(healthDeadline==DateTime.MinValue || now>healthDeadline || now<nextHealthReport)return;
            nextHealthReport=now.AddMilliseconds(500);
            try {
                var health=StartupHealth.Snapshot(settings.Paused,keyboardMonitor!=null && keyboardMonitor.Running,
                    nativeCapture.Ready64,nativeCapture.Ready32,disk.Error==null && !migrationBlocked,
                    nativeCapture.Error!=null || (keyboardMonitor!=null && keyboardMonitor.Error!=null),pid);
                StartupHealth.Save(disk.Folder,health);
                if((bool)health["Healthy"])healthDeadline=DateTime.MinValue;
            } catch {}
        }
        string StartupPath() { return liveCapture?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "拾字.lnk"):Path.Combine(disk.Folder,"offline-startup.lnk"); }
        void SetStartup(bool enabled) {
            if(!liveCapture)return;
            string path = StartupPath();
            if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type t = shortcut.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--background" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { L.T("拾字 · 本地输入历史") });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
        }
        void FlushCommittedInput() { DrainKeyboardInput();nativeCapture.FlushPending();DrainKeyboardInput();nativeCapture.Drain(ReceiveCommitted); }
        void SessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e) {
            Action flush=delegate {if(!disposed){FlushCommittedInput();if(dirty)SaveHistory();}};
            if(ui.CheckAccess()){flush();return;}
            if(ui.HasShutdownStarted || ui.HasShutdownFinished)return;
            // SystemEvents uses its own thread. Keep shutdown mutation on the
            // same dispatcher as capture and normal saves, with a bounded wait.
            try {ui.Invoke(DispatcherPriority.Send,TimeSpan.FromSeconds(2),flush);}
            catch(TimeoutException){}catch(InvalidOperationException){}
        }
        void Exit() {
            if(disposed || exitPending)return;
            FlushCommittedInput();
            if(!settings.Paused && worker!=null && !worker.HasExited) {
                exitPending=true;exitDeadline=DateTime.UtcNow.AddMilliseconds(900);exitFlushId=0;
                if(!pending){SendCapture(true);exitFlushId=requestId;}
                return;
            }
            FinishExit();
        }
        void FinishExit() {
            if(disposed)return;FlushCommittedInput();if(dirty)SaveHistory();
            if(dirty) {
                exitPending=false;exitFlushId=0;
                string message=L.T("历史尚未保存，已取消退出。请检查数据目录后重试。");Notify(message);
                if(liveCapture && (window==null || !window.IsVisible))try{tray.ShowBalloonTip(5000,L.T("拾字"),message,Forms.ToolTipIcon.Warning);}catch{}
                return;
            }
            disk.SaveSettings(settings);Dispose();ui.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        public void Dispose() {
            if (disposed) return;
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=false;
            DrainKeyboardInput();disposed = true;
            Microsoft.Win32.SystemEvents.SessionEnding -= SessionEnding;
            if (tick != null) tick.Stop(); if (toastTimer != null) toastTimer.Stop();
            nativeCapture.Dispose();
            nativeCapture.Drain(ReceiveCommitted);
            DrainAdmitted(true);
            if(dirty) SaveHistory();
            StopWorker();
            if(keyboardMonitor!=null)keyboardMonitor.Dispose();
            if (source != null) Native.UnregisterHotKey(source.Handle, 1);
            if (tray != null) { tray.Visible = false; var icon = tray.Icon; tray.Dispose(); icon.Dispose(); }
            if (showEvent != null) showEvent.Set();
        }
    }
}
