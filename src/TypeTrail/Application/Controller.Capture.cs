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
    // One controller owns the state; this file contains its capture responsibilities.
    internal sealed partial class Controller {
        void RefreshNativeCapture(bool force) {
            DrainKeyboardInput();
            nativeCapture.Drain(ReceiveCommitted);
            if(!liveCapture)return;
            string previous = nativeCapture.Targets;
            nativeCapture.Refresh(settings, force);
            // A focus-triggered subscription can finish just after the first key.
            // Forward that actual recent activity once to the new subscription.
            if (previous != nativeCapture.Targets && InputActive()) {
                nativeCapture.NotifyInputAt(lastInputProcessId,lastInputTarget,lastInputTick);
            }
        }
        void SendCapture(bool flush) {
            if (worker == null || worker.HasExited) { StartWorker(); return; }
            IntPtr foreground=Native.GetForegroundWindow();uint owner;uint thread=Native.GetWindowThreadProcessId(foreground,out owner);
            if(owner!=(uint)pid && protectionEpoch==0) {
                var gui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
                IntPtr focused=Native.GetGUIThreadInfo(thread,ref gui)?gui.hwndFocus:IntPtr.Zero;
                ApplyFocus(foreground,focused,false,0,0);EnsureProtectionFocus(foreground,focused,owner,Native.GetTickCount64(),false);
            }
            bool commitHint = lastImeCommit != 0 && (Stopwatch.GetTimestamp() - lastImeCommit) / (double)Stopwatch.Frequency < 2;
            pending = true; requestedAt = lastRequest = DateTime.UtcNow; requestedWindow=foreground; focusDirty = false;
            var inputGui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
            IntPtr inputFocus=Native.GetGUIThreadInfo(thread,ref inputGui)&&inputGui.hwndFocus!=IntPtr.Zero?inputGui.hwndFocus:foreground;
            var activity=keyboardActivity.ForRequest(owner,unchecked((ulong)foreground.ToInt64()),unchecked((ulong)inputFocus.ToInt64()),protectionEpoch,fieldAdmission.FieldFor(protectionEpoch),Native.GetTickCount64(),fieldAdmission.FieldFor,fieldAdmission.IsDenied);
            var request = new CaptureRequest { InputActivity=activity, ProtectionEpoch=protectionEpoch, MetadataOnly=pendingPaste==null, Id = ++requestId, ParentPid = pid, Epoch = epoch, Active = InputActive(), InputSerial = inputSerial, FocusSerial = focusSerial, CanInsert = inputCanInsert, ExcludedApps = settings.ExcludedApps, MaxChars = Math.Max(50000, Math.Min(500000, settings.CharacterLimit)), Flush = flush, TargetWindow = foreground.ToInt64(), ImeCommitHint = commitHint };
            if (flush) popupFlushId = request.Id;
            QueueCaptureRequest(request);
        }
        void QueueCaptureRequest(CaptureRequest request) {
            try {
                Process running=worker;StreamWriter input=workerInput;string line=json.Serialize(request);
                // A provider can stop reading its pipe during UIA/COM calls.
                // Never make the dispatcher (and all native input drains) wait
                // for that process to accept even a single request.
                if(!ThreadPool.QueueUserWorkItem(delegate {
                    try {
                        lock(input) {
                            if(disposed || running!=Interlocked.CompareExchange(ref worker,null,null))return;
                            input.WriteLine(line);input.Flush();
                        }
                    } catch {
                        try {ui.BeginInvoke(new Action(delegate {
                            if(disposed || running!=worker)return;
                            StopWorker();if(request.Flush)popupFlushId=0;
                        }));}catch(InvalidOperationException){}
                    }
                }))throw new InvalidOperationException("Capture transport unavailable");
            }
            catch { StopWorker(); if (request.Flush) popupFlushId = 0; }
        }
        void RecoverCaptureWorker() {
            if(requestedWindow==IntPtr.Zero || worker==null || exitPending)return;
            IntPtr foreground=Native.GetForegroundWindow();uint owner;
            Native.GetWindowThreadProcessId(foreground,out owner);
            // Give a healthy in-flight read a short chance to finish. A bounded
            // retirement also applies if focus returns while its drain stalls.
            if(pending && (DateTime.UtcNow-requestedAt).TotalMilliseconds<250)return;
            if(!retiringWorker && (foreground==IntPtr.Zero || foreground==requestedWindow || owner==0 || owner==(uint)pid))return;
            if(!pending && !retiringWorker) {
                // Collect already-confirmed callbacks before discarding the
                // old provider's COM cache. This request performs no UIA reads,
                // even if focus returns to that application during the drain.
                retiringWorker=true;pending=true;requestedAt=DateTime.UtcNow;
                QueueCaptureRequest(new CaptureRequest {DrainOnly=true,Id=++requestId,ParentPid=pid,Epoch=epoch,TargetWindow=requestedWindow.ToInt64(),FocusSerial=focusSerial,InputSerial=inputSerial});
                return;
            }
            Trace("worker preempted stale-window="+requestedWindow+" current-window="+foreground);
            StopWorker();focusDirty=true;lastRequest=DateTime.MinValue;
            if(popupPending)popupFlushId=0;
            StartWorker();
        }
        void StartWorker() {
            if (disposed || settings.Paused || !liveCapture) return;
            try {
                var start = new ProcessStartInfo(exe, "--worker " + pid) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                worker = new Process { StartInfo = start, EnableRaisingEvents = true };
                Process running = worker;
                running.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null || disposed) return;
                    try {
                        var sample = json.Deserialize<Sample>(e.Data);
                        ui.BeginInvoke(new Action(delegate {
                            if (disposed || running != worker) return;
                            pending = false;
                            if(retiringWorker) {
                                if(sample.Epoch==epoch && !settings.Paused && sample.Commits!=null && sample.Commits.Count>0)Receive(sample);
                                StopWorker();focusDirty=true;lastRequest=DateTime.MinValue;
                                if(popupPending)popupFlushId=0;
                                StartWorker();return;
                            }
                            if (sample.Epoch != epoch || settings.Paused) return;
                            Receive(sample);
                            if(exitPending && sample.Id==exitFlushId) { FinishExit();return; }
                            if (popupPending && sample.Id == popupFlushId) { popupPending = false; ShowCore(); }
                        }));
                    } catch { if (!disposed) ui.BeginInvoke(new Action(delegate { if (running == worker) pending = false; })); }
                };
                running.Start(); workerInput = new StreamWriter(running.StandardInput.BaseStream, new UTF8Encoding(false)); running.BeginOutputReadLine(); running.BeginErrorReadLine();
                pending = false;
            } catch { worker = null; captureStatus = L.T("采集服务未能启动，重新打开拾字可重试"); }
        }
        void StopWorker() {
            history.ResetSession();
            Process old = worker; worker = null; workerInput=null; pending = false; retiringWorker=false; requestedWindow=IntPtr.Zero;
            if (old != null) { try { if (!old.HasExited) old.Kill(); } catch {} try { old.Dispose(); } catch {} }
        }
        void Receive(Sample sample) {
            DrainKeyboardInput();
            Trace("protection epoch="+sample.ProtectionEpoch+" rootPid="+sample.ProtectionPid+" root="+sample.ProtectionWindow+" tick="+sample.ProtectionTick+" known="+sample.ProtectionKnown+" allow="+sample.ProtectionAllowed);
            if(sample.ProtectionEpoch!=0) {
                long resolved=fieldAdmission.Resolve(sample.ProtectionEpoch,sample.ProtectionPid,sample.ProtectionWindow,sample.ProtectionKnown,sample.ProtectionAllowed,sample.ProtectionField,sample.ProtectionTick);
                if(resolved!=0 && resolved!=sample.ProtectionEpoch && sample.ProtectionEpoch==protectionEpoch) {
                    SaveFieldTyping();protectionEpoch=resolved;typing.Clear();pendingPaste=null;pendingPasteBaseline=null;focusSerial++;focusDirty=true;
                }
                if((sample.ProtectionEpoch==protectionEpoch || resolved==protectionEpoch) && sample.ProtectionKnown) {
                    protectedWindow=sample.ProtectionAllowed?IntPtr.Zero:observedWindow;
                    if(!sample.ProtectionAllowed) {typing.Clear();pendingPaste=null;pendingPasteBaseline=null;}
                }
                DrainAdmitted();
            }
            Trace("read status=" + sample.Status + " focus=" + sample.FocusSerial + "/" + focusSerial + " keys=" + sample.InputSerial + "/" + inputSerial + " active=" + sample.Active + " insert=" + sample.CanInsert + " textLength=" + (sample.Text ?? "").Length + " hintLength=" + typing.Text.Length);
            if(sample.CommitOverflow){captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";}
            if(sample.Commits!=null && sample.Commits.Count>0) {
                nativeCapture.Drain(ReceiveCommitted);
                foreach(var commit in sample.Commits) {
                    if(commit.ProcessId==0 || commit.ProcessId==(uint)pid || commit.Window==0 || !NativeCapture.Allowed(commit.ProcessName,settings.ExcludedApps))continue;
                    IntPtr root=Native.GetAncestor(new IntPtr(unchecked((long)commit.Window)),2);if(root==IntPtr.Zero)root=new IntPtr(unchecked((long)commit.Window));
                    uint owner;Native.GetWindowThreadProcessId(root,out owner);if(owner==0)owner=commit.ProcessId;
                    ulong before=commit.OriginTick==0?commit.Tick:commit.OriginTick;
                    long beforeStamp=commit.OriginTimestamp==0?commit.Timestamp:commit.OriginTimestamp;
                    if(!keyboardActivity.Has(owner,unchecked((ulong)root.ToInt64()),commit.Window,commit.ActivityField,before,fieldAdmission.FieldFor,fieldAdmission.IsDenied,beforeStamp)) {Trace("UIA skipped-no-unconsumed-keyboard pid="+commit.ProcessId+" window="+commit.Window+" origin="+commit.OriginTick);continue;}
                    ReceiveCommitted(new CommittedInput {Kind=1,ProcessId=commit.ProcessId,ThreadId=commit.ThreadId,Window=commit.Window,Tick=commit.Tick,OriginTick=commit.OriginTick,Text=commit.Text,FieldIdentity=commit.FieldIdentity,Flags=24},commit.Source);
                    keyboardActivity.Consume(owner,unchecked((ulong)root.ToInt64()),commit.Window,commit.ActivityField,0,before,fieldAdmission.FieldFor,beforeStamp);
                }
                if(sample.CommitOverflow) {captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";}
                return;
            }
            if (sample.Status == "self" || !CaptureFlow.Accept(sample.FocusSerial, focusSerial, sample.InputSerial, inputSerial)) { history.LeaveField(); return; }
            if (nativeCapture.IsForegroundTarget()) {
                if(sample.ProtectionEpoch==protectionEpoch && sample.ProtectionKnown)protectedWindow=sample.ProtectionAllowed?IntPtr.Zero:observedWindow;
                history.LeaveField(); if (protectedWindow != IntPtr.Zero) typing.Clear();
                // WM_PASTE does not necessarily generate WM_CHAR or a TSF edit.
                // Confirm only the text from the user's actual paste command;
                // never import the rest of this field or a page snapshot.
                string acceptedPaste;
                if (protectedWindow == IntPtr.Zero && pendingPaste != null && pendingPasteFocus == focusSerial && sample.InputSerial >= pendingPasteSerial && ((DateTime.UtcNow - pendingPasteAt).TotalMilliseconds >= 250 || sample.Id==popupFlushId || (exitPending && sample.Id==exitFlushId)) && NativeInputEvidence.ConfirmPaste(sample, pendingPaste, pendingPasteBaseline, pendingPasteConsumed, out acceptedPaste)) {
                    IntPtr foreground = Native.GetForegroundWindow(); uint processId; uint thread = Native.GetWindowThreadProcessId(foreground, out processId);
                    var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                    IntPtr focus = Native.GetGUIThreadInfo(thread, ref gui) && gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : foreground;
                    ReceiveCommitted(new CommittedInput { Kind = 1, ProcessId = processId, ThreadId = thread, Window = unchecked((ulong)focus.ToInt64()), Tick = Native.GetTickCount64(), Text = acceptedPaste, Flags = 32|1024 }, sample.Source);
                    pendingPaste = null; pendingPasteBaseline=null; pendingPasteConsumed=0;
                }
                if (protectedWindow != IntPtr.Zero || (pendingPaste != null && (DateTime.UtcNow - pendingPasteAt).TotalSeconds > 3)) {pendingPaste = null;pendingPasteBaseline=null;}
                captureStatus = sample.Source + (protectedWindow != IntPtr.Zero ? L.T(" · 已跳过敏感或只读输入区") : L.T(" · 正在监听上屏文字"));
                captureState = protectedWindow != IntPtr.Zero ? "protected" : "ok";
                if(sample.CommitOverflow){captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";}
                DrainAdmitted();
                return;
            }
            // A temporary hook failure must not switch to a different snapshot
            // journal: that saved preedit pinyin and split the current sentence.
            // Samples remain useful for focus/protection and paste confirmation.
            if (sample.Status == "protected" || sample.Status == "excluded" || sample.Status == "readonly" || !InputActive()) typing.Clear();
            captureState = sample.Status;
            switch (sample.Status) {
                case "ok": captureStatus = sample.Source + L.T(" · 输入框可记录"); break;
                case "baseline": captureStatus = sample.Source + L.T(" · 等待输入"); break;
                case "uncertain_input": captureStatus = sample.Source + L.T(" · 无法确定输入边界，本次未记录"); break;
                case "composing": captureStatus = sample.Source + L.T(" · 正在组词，确认后记录"); break;
                case "protected": captureStatus = sample.Source + L.T(" · 已跳过密码或敏感输入框"); break;
                case "readonly": captureStatus = sample.Source + L.T(" · 当前文档为只读"); break;
                case "excluded": captureStatus = sample.Source + L.T(" · 已在排除列表中"); break;
                case "unsupported": captureStatus = sample.Source + L.T(" · 当前焦点不在可读取的输入区"); break;
                case "unsupported_input": captureStatus = sample.Source + L.T(" · 此编辑区未提供可读取的文本，当前未记录"); break;
                case "office_unavailable": captureStatus = sample.Source + L.T(" · 文档接口暂不可用，当前未记录"); break;
                case "unavailable": captureStatus = sample.Source + L.T(" · 暂时无法读取输入框"); break;
                default: captureStatus = L.T("等待输入"); break;
            }
            ApplyNativeStatus();
            if(sample.CommitOverflow){captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";}
            DrainAdmitted();
            if (window != null && window.IsVisible) { UpdateUsage(); UpdatePause(); }
        }
        void ApplyNativeStatus() {
            if(nativeCapture.TakeLoss()) {captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";}
            else if(nativeCapture.Error!=null && captureState!="partial") {captureStatus=nativeCapture.Error;captureState="capture_error";}
            else if(nativeCapture.Error==null && captureState=="capture_error" && (keyboardMonitor==null || keyboardMonitor.Error==null)) {
                captureStatus=L.T("输入监听已恢复，等待输入");captureState="idle";
            }
            if(captureState!="partial" && nativeCapture.Error==null && (keyboardMonitor==null || keyboardMonitor.Error==null) && fieldAdmission.IsDeferred(protectionEpoch,Native.GetTickCount64())) {
                captureStatus=L.T("输入窗口响应较慢，未确认的文字暂存在内存中，正在重试");captureState="confirming";
            }
        }
        void PrepareCapture() {if(liveCapture){nativeCapture.Prewarm(settings);nativeCapture.Refresh(settings,true);}}
    }
}
