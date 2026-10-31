using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace TypeTrail {
    internal static class CaptureWorker {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        static AutomationFocusChangedEventHandler focusHandler;
        static readonly ImeState Ime = new ImeState();
        static readonly OfficeCapture Office = new OfficeCapture();
        public static void Run(int parentPid) {
            Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            // An isolated MTA process keeps slow/broken providers away from the popup.
            var watchdog = new Timer(delegate {
                try { using (var parent = Process.GetProcessById(parentPid)) if (parent.HasExited) Environment.Exit(0); } catch { Environment.Exit(0); }
            }, null, 2000, 2000);
            try { focusHandler = delegate(object sender, AutomationFocusChangedEventArgs e) {}; Automation.AddAutomationFocusChangedEventHandler(focusHandler); } catch {}
            string line;
            while ((line = Console.ReadLine()) != null) {
                try {
                    var request = Json.Deserialize<CaptureRequest>(line);
                    var result = Read(request);
                    if (request.Flush) {
                        // Give TSF and asynchronous browser accessibility updates a short
                        // opportunity to publish the committed text before the popup takes focus.
                        Thread.Sleep(70);
                        var latest = Read(request);
                        result = CommittedCapture.Merge(result, latest);
                    }
                    result.Id = request.Id; result.Epoch = request.Epoch; result.Active = request.Active; result.ImeCommitHint = request.ImeCommitHint;
                    result.InputSerial = request.InputSerial; result.FocusSerial = request.FocusSerial; result.CanInsert = request.CanInsert;
                    ProtectionMetadata.Attach(result,request);
                    result.CommitOverflow = Ime.CommitOverflow;
                    if (request.Diagnostics) result.Diagnostic = DescribeFocus();
                    Console.WriteLine(Json.Serialize(result)); Console.Out.Flush();
                } catch { Console.WriteLine("{\"Status\":\"unavailable\"}"); Console.Out.Flush(); }
            }
            watchdog.Dispose();
            Ime.Dispose();
            Office.Dispose();
        }
        static Sample State(string status, string source) { return new Sample { Status = status, Source = source }; }
        static string DescribeFocus() {
            var details = new StringBuilder();
            try {
                uint pid; uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
                var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                if (Native.GetGUIThreadInfo(thread, ref gui)) {
                    var cls = new StringBuilder(128); Native.GetClassName(gui.hwndFocus, cls, cls.Capacity);
                    details.Append("NativeFocus=" + gui.hwndFocus + ";Class=" + cls + "; ");
                }
                var element = AutomationElement.FocusedElement;
                for (int i = 0; i < 3 && element != null; i++) {
                    var current = element.Current;
                    details.Append(current.ControlType.ProgrammaticName + "/" + current.ClassName + ";Keyboard=" + current.IsKeyboardFocusable + ";Patterns=" + String.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName)) + " | ");
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            } catch (Exception e) { details.Append(e.GetType().Name); }
            return details.ToString();
        }
        static Sample Read(CaptureRequest request) {
            // Finalizations already admitted by the previous focused editor are
            // events, not snapshots of the current foreground. Drain them first.
            var delayed = CommittedCapture.Sample(Ime.TakeCommittedInputs());
            if (delayed != null) return delayed;
            IntPtr foreground = Native.GetForegroundWindow();
            if (request.TargetWindow != 0 && foreground.ToInt64() != request.TargetWindow) return State("unavailable", "");
            uint pid; uint thread = Native.GetWindowThreadProcessId(foreground, out pid);
            if (pid == 0) return State("idle", "");
            if (pid == request.ParentPid || pid == Process.GetCurrentProcess().Id) return State("self", "");
            string process;
            try { using (var p = Process.GetProcessById((int)pid)) process = p.ProcessName; } catch { return State("unavailable", ""); }
            if (!OfficeCapture.Supports(process)) Office.Dispose();
            string source = Friendly(process);
            string[] excluded = ((request.ExcludedApps ?? "") + ",LockApp,LogonUI,CredentialUIBroker,Consent").Split(new[] { ',', ';', '\n', '\r', '，' }, StringSplitOptions.RemoveEmptyEntries);
            if (excluded.Any(n => String.Equals(n.Trim().Replace(".exe", ""), process, StringComparison.OrdinalIgnoreCase))) return State("excluded", source);
            bool editableHint = false;
            try {
                var element = AutomationElement.FocusedElement;
                if (element == null) throw new ElementNotAvailableException();
                var info = element.Current;
                uint focusedPid=0;
                var focusedGui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
                if(Native.GetGUIThreadInfo(thread,ref focusedGui) && focusedGui.hwndFocus!=IntPtr.Zero && Native.GetAncestor(focusedGui.hwndFocus,2)==Native.GetAncestor(foreground,2))Native.GetWindowThreadProcessId(focusedGui.hwndFocus,out focusedPid);
                // Foreground self-windows were rejected above. A stale UIA focus
                // pointing at our old popup must not block the native fallbacks.
                if ((info.ProcessId != pid && info.ProcessId != focusedPid) || !BelongsToWindow(element, foreground)) throw new ElementNotAvailableException();
                if (info.IsPassword || SensitiveName(info.Name)) return State("protected", source);
                // Check parent containers before requesting any text.
                var ancestor = element;
                for (int i = 0; i < 3; i++) {
                    ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
                    if (ancestor == null || ancestor.Current.ControlType == ControlType.Window) break;
                    if (ancestor.Current.IsPassword) return State("protected", source);
                }
                var candidate = element;
                for (int depth = 0; depth < 3 && candidate != null; depth++) {
                    var c = candidate.Current;
                    if (c.ProcessId != pid && c.ProcessId != focusedPid) break;
                    if (c.IsPassword || SensitiveName(c.Name)) return State("protected", source);
                    // Never climb from an inaccessible input into a whole webpage or
                    // conversation document and mistake that container for the editor.
                    if (depth > 0 && c.ControlType == ControlType.Document) break;
                    bool editableType = IsEditableCandidate(c.ControlType, depth, c.IsKeyboardFocusable);
                    editableHint |= editableType;
                    object pattern;
                    string text = null; bool truncated = false;
                    string runtimeId = String.Join(".", candidate.GetRuntimeId());
                    bool compositionFinalized;
                    int composition = Ime.Read(runtimeId, out compositionFinalized, CompositionSource(foreground, pid, thread, source, process), !request.MetadataOnly,request.InputActivity,c.ProcessId+":"+runtimeId);
                    var committed = CommittedCapture.Sample(Ime.TakeCommittedInputs());
                    if (committed != null) return committed;
                    if (composition < 0) composition = ImmComposition(thread);
                    if (composition == 1) {
                        return new Sample { Status = "composing", Source = source, Key = pid + ":" + runtimeId, Text = Ime.CompositionBase, HasCompositionBase = Ime.CompositionBase != null };
                    }
                    if(request.MetadataOnly) {
                        // Native capture owns the journal. Keep finalization and
                        // password metadata live without copying the document.
                        if((c.ControlType==ControlType.Edit || c.ControlType==ControlType.ComboBox) && candidate.TryGetCurrentPattern(ValuePattern.Pattern,out pattern) && ((ValuePattern)pattern).Current.IsReadOnly)return State("readonly",source);
                        return new Sample {Status=editableType?"ok":"unsupported",Source=source,Key=pid+":"+runtimeId};
                    }
                    // Prefer the live document range: some Chromium Value providers lag
                    // behind the committed IME text that TextPattern already exposes.
                    if (candidate.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
                        var document = ((TextPattern)pattern).DocumentRange;
                        object readOnly = document.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                        if (readOnly is bool && !(bool)readOnly && editableType) {
                            var tail = document.Clone();
                            tail.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.End);
                            tail.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -request.MaxChars);
                            truncated = tail.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) != 0;
                            text = tail.GetText(request.MaxChars);
                        }
                    }
                    if (text == null && candidate.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) {
                        var value = (ValuePattern)pattern;
                        if (!value.Current.IsReadOnly && editableType) {
                            string full = value.Current.Value ?? "";
                            truncated = full.Length > request.MaxChars;
                            text = History.Tail(full, request.MaxChars);
                        }
                    }
                    if (text != null) {
                        // Office canvases may advertise an empty/custom Value provider.
                        // Their document model is authoritative; toolbar edit boxes keep UIA.
                        if (OfficeCapture.Supports(process) && c.ControlType != ControlType.Edit && c.ControlType != ControlType.ComboBox) break;
                        // Some WinForms RichEdit UIA providers return an empty
                        // Value while their standard native editor has content.
                        // The checked native edit also supplies an exact caret.
                        var nativeGui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
                        if(Native.GetGUIThreadInfo(thread,ref nativeGui) && nativeGui.hwndFocus!=IntPtr.Zero && Native.GetAncestor(nativeGui.hwndFocus,2)==Native.GetAncestor(foreground,2)) {
                            var nativeEdit=NativeEditCapture.Read(nativeGui.hwndFocus,pid,source,request.MaxChars);
                            if(nativeEdit!=null && foreground==Native.GetForegroundWindow()) return nativeEdit;
                        }
                        // Discard stale results if focus moved while a provider was answering.
                        if (foreground != Native.GetForegroundWindow()) return State("unavailable", source);
                        return new Sample { Status = "ok", Source = source, Text = text, Key = pid + ":" + runtimeId, Truncated = truncated, CompositionFinalized = compositionFinalized, Caret = CaretOffset(candidate, text, truncated) };
                    }
                    if (c.ControlType == ControlType.Window || c.ControlType == ControlType.Document) break;
                    candidate = TreeWalker.ControlViewWalker.GetParent(candidate);
                }
            } catch (ElementNotAvailableException) {}
              catch (UnauthorizedAccessException) { return State("protected", source); }
              catch {}
            if(request.MetadataOnly)return State(editableHint?"ok":"unsupported",source);
            // Legacy Win32 Edit/RichEdit controls sometimes lack a usable UIA provider.
            bool officeAttempted = false;
            var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (Native.GetGUIThreadInfo(thread, ref gui) && gui.hwndFocus != IntPtr.Zero) {
                if (Native.GetAncestor(gui.hwndFocus, 2) != Native.GetAncestor(foreground, 2)) return State("unavailable", source);
                var name = new StringBuilder(128); Native.GetClassName(gui.hwndFocus, name, name.Capacity);
                string cls = name.ToString();
                if (cls.Equals("Scintilla", StringComparison.OrdinalIgnoreCase)) {
                    editableHint = true;
                    var scintilla = ScintillaCapture.Read(gui.hwndFocus, pid, source, request.MaxChars);
                    if (scintilla != null && Native.GetForegroundWindow() == foreground) return scintilla;
                }
                var nativeFallback=NativeEditCapture.Read(gui.hwndFocus,pid,source,request.MaxChars);
                if(nativeFallback!=null && Native.GetForegroundWindow()==foreground) return nativeFallback;
                if (OfficeCapture.Supports(process)) {
                    officeAttempted = true;
                    var document = ReadOffice(process, foreground, request.MaxChars);
                    if (document != null) return document;
                }
                var legacy = LegacyCapture.Read(gui.hwndFocus, pid, source, request.MaxChars);
                if (legacy != null && Native.GetForegroundWindow() == foreground) return legacy;
            }
            if (OfficeCapture.Supports(process)) {
                var document = officeAttempted ? null : ReadOffice(process, foreground, request.MaxChars);
                if (document != null) return document;
                return State("office_unavailable", source);
            }
            return State(editableHint || request.Active ? "unsupported_input" : "unsupported", source);
        }
        static int CaretOffset(AutomationElement element, string text, bool truncated) {
            if (truncated) return -1;
            try {
                object pattern;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return -1;
                var provider = (TextPattern)pattern;
                var selections = provider.GetSelection();
                if (selections.Length != 1 || selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selections[0], TextPatternRangeEndpoint.End) != 0) return -1;
                var prefix = provider.DocumentRange.Clone();
                prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
                int length = History.Normalize(prefix.GetText(text.Length + 1)).Length;
                return length <= History.Normalize(text).Length ? length : -1;
            } catch { return -1; }
        }
        static FinalizedInput CompositionSource(IntPtr foreground, uint pid, uint thread, string source, string process) {
            IntPtr focus = foreground;
            var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (Native.GetGUIThreadInfo(thread, ref gui) && gui.hwndFocus != IntPtr.Zero && Native.GetAncestor(gui.hwndFocus, 2) == Native.GetAncestor(foreground, 2)) {
                uint owner; uint focusThread = Native.GetWindowThreadProcessId(gui.hwndFocus, out owner);
                if (owner != 0) {
                    focus=gui.hwndFocus;thread=focusThread;
                    if(owner!=pid) {
                        try {using(var focusedProcess=Process.GetProcessById((int)owner))process=focusedProcess.ProcessName;}
                        catch {return null;}
                        pid=owner;
                    }
                }
            }
            return new FinalizedInput { ProcessId = pid, ThreadId = thread, Window = unchecked((ulong)focus.ToInt64()), Source = source, ProcessName = process };
        }
        internal static bool IsEditableCandidate(ControlType type, int depth, bool keyboardFocusable) {
            if (depth > 0 && type == ControlType.Document) return false;
            return type == ControlType.Edit || type == ControlType.ComboBox || ((type == ControlType.Document || type == ControlType.Custom || type == ControlType.Pane) && keyboardFocusable);
        }
        internal static bool BelongsToWindow(AutomationElement element, IntPtr foreground) {
            for (int i = 0; i < 16 && element != null; i++) {
                int handle = element.Current.NativeWindowHandle;
                if (handle != 0) return Native.GetAncestor(new IntPtr(handle), 2) == Native.GetAncestor(foreground, 2);
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            return false;
        }
        static Sample ReadOffice(string process, IntPtr foreground, int maxChars) {
            var document = Office.Read(process, foreground, maxChars);
            if (document != null && Native.GetForegroundWindow() == foreground) return document;
            return null;
        }
        static int ImmComposition(uint thread) {
            var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (!Native.GetGUIThreadInfo(thread, ref gui) || gui.hwndFocus == IntPtr.Zero) return -1;
            IntPtr context = Native.ImmGetContext(gui.hwndFocus);
            if (context == IntPtr.Zero) return -1;
            try { int bytes = Native.ImmGetCompositionStringW(context, 8, IntPtr.Zero, 0); return bytes > 0 ? 1 : bytes == 0 ? 0 : -1; }
            finally { Native.ImmReleaseContext(gui.hwndFocus, context); }
        }
        internal static bool SensitiveName(string name) {
            return SensitiveFieldRules.Name(name);
        }
        static string Friendly(string process) {
            switch (process.ToLowerInvariant()) {
                case "notepad": return "记事本";
                case "msedge": return "Microsoft Edge";
                case "chrome": return "Google Chrome";
                case "firefox": return "Firefox";
                case "brave": return "Brave";
                case "weixin": case "wechat": return "微信";
                case "code": return "Visual Studio Code";
                case "winword": return "Microsoft Word";
                case "wps": return "WPS 文字";
                case "typetrail": return "拾字测试输入框";
                default: return process;
            }
        }
    }
}
