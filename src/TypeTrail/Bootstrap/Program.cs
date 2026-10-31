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
    internal static class Program {
        internal static bool TryWriteStartupError(string folder,Exception error) {
            try {File.WriteAllText(Path.Combine(folder,"last-error.txt"),error.ToString());return true;}
            catch {return false;} // A failed diagnostic write must not hide the original startup failure.
        }
        [STAThread]
        public static void Main(string[] args) {
            if (args.Length > 1 && args[0] == "--worker") {
                int pid = Int32.Parse(args[1]);
                var t = new Thread(delegate() { CaptureWorker.Run(pid); }); t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join(); return;
            }
            // Unsupported development switches must not start recording.
            if (args.Contains("--self-test") || args.Contains("--test-host") || args.Contains("--offline-test")) { Environment.ExitCode = 2; return; }
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeTrail");
            int dataIndex = Array.IndexOf(args, "--data");
            if (dataIndex >= 0 && args.Length > dataIndex + 1) folder = Path.GetFullPath(args[dataIndex + 1]);
            folder = ProfileIdentity.Normalize(folder);
            string key = ProfileIdentity.Key(folder);
            if (args.Contains("--exit")) { try { using (var ev = EventWaitHandle.OpenExisting(key + "-exit")) ev.Set(); } catch {} return; }
            bool created;
            using (var mutex = new Mutex(true, key, out created)) {
                if (!created) {
                    if(args.Contains("--write-health"))
                        try { using(var ev=EventWaitHandle.OpenExisting(key+"-health"))ev.Set(); } catch {}
                    int lang = Array.IndexOf(args, "--lang");
                    if (lang >= 0 && args.Length > lang + 1 && new[] { "en", "zh", "auto" }.Contains(args[lang + 1]))
                        try { using (var ev = EventWaitHandle.OpenExisting(key + "-lang-" + args[lang + 1])) ev.Set(); } catch {}
                    if(StartupHealth.ShouldShowExisting(args))try { using (var ev = EventWaitHandle.OpenExisting(key + "-show")) ev.Set(); } catch {}
                    return;
                }
                try {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var controller = new Controller(folder, key, args.Contains("--show"), args.Contains("--background"));
                    dispatcher.UnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { e.Handled = true; try { File.WriteAllText(Path.Combine(folder, "last-error.txt"), e.Exception.GetType().FullName + "\n" + e.Exception.StackTrace); } catch {} controller.Notify(L.T("操作未完成，请重试。历史仍保留在本机。")); };
                    Dispatcher.Run();
                    controller.Dispose();
                } catch (Exception e) { Environment.ExitCode=1;TryWriteStartupError(folder,e);MessageBox.Show(L.T("拾字未能启动：") + e.Message, L.T("拾字")); }
                mutex.ReleaseMutex();
            }
        }
    }
}
