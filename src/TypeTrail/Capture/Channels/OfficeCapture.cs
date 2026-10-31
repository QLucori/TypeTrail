using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TypeTrail {
    // Read-only document-model fallback for editors whose canvas has no usable
    // UIA text pattern. Never creates an Office instance or changes a selection.
    internal sealed class OfficeCapture : IDisposable {
        readonly Dictionary<string, object> apps = new Dictionary<string, object>();
        readonly Dictionary<string, DateTime> failed = new Dictionary<string, DateTime>();
        public static bool Supports(string process) {
            return process.Equals("wps", StringComparison.OrdinalIgnoreCase) || process.Equals("winword", StringComparison.OrdinalIgnoreCase);
        }
        public Sample Read(string process, IntPtr target, int maxChars) {
            if (!Supports(process)) return null;
            string programId = process.Equals("wps", StringComparison.OrdinalIgnoreCase) ? "kwps.Application" : "Word.Application";
            string source = programId == "kwps.Application" ? "WPS 文字" : "Microsoft Word";
            object app;
            if (!apps.TryGetValue(programId, out app)) {
                DateTime previous;
                if (failed.TryGetValue(programId, out previous) && (DateTime.UtcNow - previous).TotalSeconds < 5) return null;
                try { app = Marshal.GetActiveObject(programId); apps[programId] = app; }
                catch { failed[programId] = DateTime.UtcNow; return null; }
            }
            try {
                using (var scope = new ComScope()) {
                    object window = scope.Get(app, "ActiveWindow");
                    IntPtr windowHandle = new IntPtr(Convert.ToInt64(scope.Get(window, "Hwnd"), CultureInfo.InvariantCulture));
                    if (!SameWindow(windowHandle, target)) return null;
                    if (!Native.IsWindowVisible(windowHandle)) return null;
                    // WPS can host several document types under one outer frame.
                    // Match the focused document child, not just that shared frame.
                    if (Native.GetForegroundWindow() == Native.GetAncestor(target, 2)) {
                        var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                        if (!Native.GetGUIThreadInfo(0, ref gui) || (gui.hwndFocus != windowHandle && !Native.IsChild(windowHandle, gui.hwndFocus))) return null;
                    }
                    object document = scope.Get(app, "ActiveDocument");
                    if (Convert.ToBoolean(scope.Get(document, "ReadOnly"), CultureInfo.InvariantCulture)) return new Sample { Status = "readonly", Source = source };
                    object selection = scope.Get(window, "Selection");
                    object paragraphs = scope.Get(selection, "Paragraphs");
                    object paragraph = scope.Get(paragraphs, "Item", 1);
                    object range = scope.Get(paragraph, "Range");
                    int start = Convert.ToInt32(scope.Get(range, "Start"), CultureInfo.InvariantCulture);
                    int end = Convert.ToInt32(scope.Get(range, "End"), CultureInfo.InvariantCulture);
                    bool truncated = end - start > maxChars;
                    if (truncated) {
                        range = scope.Get(range, "Duplicate");
                        range.GetType().InvokeMember("Start", BindingFlags.SetProperty, null, range, new object[] { end - maxChars });
                    }
                    string text = Convert.ToString(scope.Get(range, "Text"), CultureInfo.InvariantCulture) ?? "";
                    text = text.TrimEnd('\r', '\a');
                    string identity = Convert.ToString(scope.Get(document, "FullName"), CultureInfo.InvariantCulture);
                    int story = Convert.ToInt32(scope.Get(range, "StoryType"), CultureInfo.InvariantCulture);
                    uint pid; Native.GetWindowThreadProcessId(windowHandle, out pid);
                    return new Sample { Status = "ok", Source = source, Key = pid + ":office:" + Hash(identity) + ":" + story + ":" + start, Text = History.Tail(text, maxChars), Truncated = truncated };
                }
            } catch {
                Release(app); apps.Remove(programId); failed[programId] = DateTime.UtcNow;
                return null;
            }
        }
        static bool SameWindow(IntPtr document, IntPtr target) {
            if (document == IntPtr.Zero || target == IntPtr.Zero || !Native.IsWindow(document) || !Native.IsWindow(target)) return false;
            return document == target || Native.IsChild(target, document) || Native.GetAncestor(document, 2) == Native.GetAncestor(target, 2);
        }
        static string Hash(string value) {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")), 0, 12).Replace("-", "");
        }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) try { Marshal.ReleaseComObject(value); } catch {} }
        public void Dispose() { foreach (var app in apps.Values) Release(app); apps.Clear(); }
        sealed class ComScope : IDisposable {
            readonly List<object> values = new List<object>();
            public object Get(object parent, string name, params object[] args) {
                object result = parent.GetType().InvokeMember(name, BindingFlags.GetProperty, null, parent, args);
                if (result != null && Marshal.IsComObject(result)) values.Add(result);
                return result;
            }
            public void Dispose() { for (int i = values.Count - 1; i >= 0; i--) Release(values[i]); }
        }
    }
}
