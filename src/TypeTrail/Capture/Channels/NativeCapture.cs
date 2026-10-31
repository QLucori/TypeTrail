using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TypeTrail {
    // Automatically follow the foreground input owner; no application allowlist.
    internal sealed class NativeCapture : IDisposable {
        NativeInputBridge bridge;
        X86InputBridge companion;
        string signature, error;
        string metadataError;
        string metadataWarning;
        string flushWarning;
        bool dataLoss;
        NativeInputBridge lossBridge;
        X86InputBridge lossCompanion;
        uint observedNativeDropped;
        int observedCompanionDropped;
        IntPtr boundRoot;
        readonly Dictionary<uint, string> sources = new Dictionary<uint, string>();
        readonly Dictionary<uint,string> nativeNames=new Dictionary<uint,string>();
        readonly Dictionary<uint,bool> source32=new Dictionary<uint,bool>();
        string exclusions="", bootstrapPolicy;
        bool bootstrap64;
        readonly HashSet<uint> hookedPids64 = new HashSet<uint>(), hookedThreads64 = new HashSet<uint>(), hookedPids32 = new HashSet<uint>(), hookedThreads32 = new HashSet<uint>();
        readonly Dictionary<uint,ulong> processBirths = new Dictionary<uint,ulong>(), threadBirths = new Dictionary<uint,ulong>();
        readonly HashSet<uint> unavailablePids = new HashSet<uint>();
        readonly Queue<KeyValuePair<CommittedInput,string>> finished = new Queue<KeyValuePair<CommittedInput,string>>();
        readonly uint ownerPid = (uint)Process.GetCurrentProcess().Id;
        DateTime nextRefresh;
        sealed class ActivitySink {
            public NativeInputBridge Native;
            public X86InputBridge Companion;
            public uint[] Pids;
        }
        ActivitySink[] activitySinks=new ActivitySink[0];
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        void PublishActivity() {
            var sinks=new List<ActivitySink>();
            if(bridge!=null)sinks.Add(new ActivitySink {Native=bridge,Pids=bootstrap64?null:hookedPids64.Where(p=>!unavailablePids.Contains(p)).ToArray()});
            if(companion!=null)sinks.Add(new ActivitySink {Companion=companion,Pids=hookedPids32.Where(p=>!unavailablePids.Contains(p)).ToArray()});
            Interlocked.Exchange(ref activitySinks,sinks.ToArray());
        }
        public bool Prewarm(int timeoutMilliseconds=2500) {
            try {if(companion==null)companion=new X86InputBridge();bool result=companion.Warm(Math.Max(0,Math.Min(5000,timeoutMilliseconds)));PublishActivity();return result;}
            catch(Exception e){error=L.T("输入监听暂不可用（")+e.GetType().Name+L.T("）");return false;}
        }
        public bool Prewarm(Settings settings,int timeoutMilliseconds=2500) {
            if(settings.Paused)return false;
            exclusions=settings.ExcludedApps??"";
            try {
                if(bridge==null){var next=new NativeInputBridge();try{next.StartBootstrap(exclusions);bridge=next;bootstrap64=true;}catch{next.Dispose();throw;}}
                else if(!bootstrap64){bridge.StartBootstrap(exclusions);bootstrap64=true;}
                else if(bootstrap64 && bootstrapPolicy!=exclusions)bridge.SetBootstrapPolicy(exclusions);
                if(companion==null)companion=new X86InputBridge();
                bool ready=companion.Bootstrap(exclusions,Math.Max(0,Math.Min(5000,timeoutMilliseconds)));
                bootstrapPolicy=exclusions;PublishActivity();return bootstrap64 && ready;
            }catch(Exception e){error=L.T("输入监听暂不可用（")+e.GetType().Name+L.T("）");PublishActivity();return false;}
        }
        // Safe on the keyboard monitor thread: immutable snapshots, no process
        // discovery or blocking pipe writes, and retain the event's original age.
        public bool PulseKeyboard(uint pid,IntPtr window,ulong originalTick) {
            if(pid==0 || pid==ownerPid)return false;
            bool accepted=false;
            foreach(var sink in Interlocked.CompareExchange(ref activitySinks,null,null)) {
                if(sink.Pids!=null && Array.IndexOf(sink.Pids,pid)<0 && (sink.Companion==null || !sink.Companion.BootstrapReady))continue;
                if(sink.Native!=null)accepted=sink.Native.NotifyInputAt(pid,unchecked((ulong)window.ToInt64()),originalTick)||accepted;
                if(sink.Companion!=null && sink.Companion.IsTarget(pid))accepted=sink.Companion.NotifyInputAt(pid,unchecked((ulong)window.ToInt64()),originalTick)||accepted;
            }
            return accepted;
        }
        string HookError { get { return error ?? (companion == null ? null : companion.Error); } }
        public string Error { get { return HookError ?? metadataError; } }
        public string Warning { get { return metadataWarning ?? flushWarning ?? (companion==null?null:companion.Warning); } }
        public bool TakeLoss() {bool result=dataLoss;dataLoss=false;return result;}
        public uint[] Diagnostics { get { return bridge == null ? new uint[0] : bridge.Diagnostics; } }
        public string Targets { get { return signature ?? ""; } }
        internal bool Ready64 { get { return bootstrap64 && bridge != null && bridge.BootstrapReady; } }
        internal bool Ready32 { get { return companion != null && companion.BootstrapReady; } }
        public void NotifyInput(uint pid, IntPtr window) {
            PulseKeyboard(pid,window,GetTickCount64());
        }
        public void NotifyInputAt(uint pid,IntPtr window,ulong originalTick) {PulseKeyboard(pid,window,originalTick);}
        public bool IsTarget(uint pid) {
            if(unavailablePids.Contains(pid)||!sources.ContainsKey(pid))return false;
            bool bits32;
            if(source32.TryGetValue(pid,out bits32))return bits32?companion!=null&&companion.IsTarget(pid):bridge!=null&&(bootstrap64||hookedPids64.Contains(pid));
            return hookedPids32.Contains(pid)?companion!=null&&companion.IsTarget(pid):bridge!=null&&hookedPids64.Contains(pid);
        }
        public static bool Supports(string process) {
            if (String.IsNullOrWhiteSpace(process)) return false;
            return !new[] { "TypeTrail", "TypeTrail.InputHost.x86", "LockApp", "LogonUI", "CredentialUIBroker", "Consent" }.Any(n => String.Equals(n, process, StringComparison.OrdinalIgnoreCase));
        }
        internal static bool Allowed(string process, string exclusions) {
            if (!Supports(process)) return false;
            return !(exclusions ?? "").Split(new[] { ',', ';', '\n', '\r', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(n => String.Equals(n.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Trim().Substring(0, n.Trim().Length - 4) : n.Trim(), process, StringComparison.OrdinalIgnoreCase));
        }
        internal static string SourceName(string process) {
            switch (process.ToLowerInvariant()) {
                case "weixin": case "wechat": return "微信";
                case "qq": return "QQ";
                case "brave": return "Brave";
                case "chrome": return "Google Chrome";
                case "msedge": return "Microsoft Edge";
                case "firefox": return "Firefox";
                case "code": return "Visual Studio Code";
                case "notepad": return "记事本";
                case "wps": return "WPS 文字";
                case "winword": return "Microsoft Word";
                default: return process;
            }
        }
        public bool IsForegroundTarget() { uint pid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid); return IsTarget(pid); }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool WindowEnum(IntPtr window, IntPtr value);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, WindowEnum callback, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsWow64Process(IntPtr process, out bool wow64);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint access,bool inherit,uint tid);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle,out FileTime created,out FileTime exited,out FileTime kernel,out FileTime user);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadTimes(IntPtr handle,out FileTime created,out FileTime exited,out FileTime kernel,out FileTime user);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetProcessIdOfThread(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeThread(IntPtr handle,out uint code);
        [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low,High;public ulong Value {get{return ((ulong)High<<32)|Low;}} }
        internal sealed class ProcessMetadata { public bool Bits32;public ulong Created; }
        static ProcessMetadata ReadProcess(uint pid) {
            IntPtr process=OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            try {
                bool wow64;uint exit;FileTime created,exited,kernel,user;
                if (!IsWow64Process(process, out wow64) || !GetProcessTimes(process,out created,out exited,out kernel,out user) || !GetExitCodeProcess(process,out exit)) throw new System.ComponentModel.Win32Exception();
                if(exit!=259)throw new InvalidOperationException("Process has exited");
                return new ProcessMetadata {Bits32=wow64,Created=created.Value};
            }finally{CloseHandle(process);}
        }
        internal static Dictionary<uint,ProcessMetadata> ReadProcesses(IEnumerable<uint> pids) {
            var result=new Dictionary<uint,ProcessMetadata>();
            foreach(uint pid in pids.Distinct())try{result[pid]=ReadProcess(pid);}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
            return result;
        }
        internal static Dictionary<uint,ulong> ReadThreads(IDictionary<uint,uint> threads) {
            var result=new Dictionary<uint,ulong>();
            foreach(var thread in threads) {
                IntPtr handle=OpenThread(0x0800,false,thread.Key);
                if(handle==IntPtr.Zero)continue;
                try {uint exit;FileTime created,exited,kernel,user;
                    if(GetProcessIdOfThread(handle)==thread.Value && GetExitCodeThread(handle,out exit) && exit==259 && GetThreadTimes(handle,out created,out exited,out kernel,out user))result[thread.Key]=created.Value;
                }finally{CloseHandle(handle);}
            }
            return result;
        }
        internal static bool ReusedIdentity(IDictionary<uint,ulong> previous,IDictionary<uint,ulong> current) {
            foreach(var pair in current) {ulong created;if(previous.TryGetValue(pair.Key,out created) && created!=pair.Value)return true;}
            return false;
        }
        internal static uint[] SelectThreads(IDictionary<uint,uint> threads,uint foregroundThread,uint focusThread) {
            return threads.Keys.OrderBy(t=>t==foregroundThread?0:t==focusThread?1:2).ThenBy(t=>t).ToArray();
        }
        public void Refresh(Settings settings, bool force) {
            if (settings.Paused) { Dispose(); return; }
            exclusions=settings.ExcludedApps??"";
            if(!bootstrap64 || companion==null || bootstrapPolicy!=exclusions)Prewarm(settings,0);
            IntPtr foreground = Native.GetForegroundWindow();
            uint foregroundPid;
            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out foregroundPid);
            // Keep the last editor's subscriptions while our panel is open.
            if (foregroundPid == ownerPid || foregroundPid == 0) return;
            if (!force && foreground == boundRoot && DateTime.UtcNow < nextRefresh) return;
            nextRefresh = DateTime.UtcNow.AddSeconds(1);
            try {
                var found = new Dictionary<uint, string>();
                var foundNames=new Dictionary<uint,string>();
                Func<uint, bool> include = delegate(uint pid) {
                    if (pid == 0 || pid == ownerPid) return false;
                    if (found.ContainsKey(pid)) return true;
                    try {
                        using (var process = Process.GetProcessById((int)pid)) {
                            if (!Allowed(process.ProcessName, settings.ExcludedApps)) return false;
                            foundNames[pid]=process.ProcessName;found[pid] = SourceName(process.ProcessName); return true;
                        }
                    } catch { return false; }
                };
                if (!include(foregroundPid)) { FlushPending(); boundRoot = foreground; return; }
                var threads = new Dictionary<uint, uint>();
                uint focusThread=0;
                threads[foregroundThread] = foregroundPid;
                var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                if (Native.GetGUIThreadInfo(foregroundThread, ref gui) && gui.hwndFocus != IntPtr.Zero) {
                    uint pid; uint thread = Native.GetWindowThreadProcessId(gui.hwndFocus, out pid);
                    if (Native.GetAncestor(gui.hwndFocus, 2) == Native.GetAncestor(foreground, 2) && include(pid)) {threads[thread] = pid;focusThread=thread;}
                }
                WindowEnum child = delegate(IntPtr window, IntPtr unused) {
                    uint pid; uint thread = Native.GetWindowThreadProcessId(window, out pid);
                    if (thread != 0 && include(pid)) threads[thread] = pid;
                    return true;
                };
                EnumChildWindows(foreground, child, IntPtr.Zero);
                // Include hidden TSF GUI threads belonging to this application.
                IntPtr message = IntPtr.Zero;
                var visitedMessages=new HashSet<IntPtr>();var enumerationClock=Stopwatch.StartNew();bool enumerationChanged=false;
                for (;;) {
                    message = FindWindowEx(new IntPtr(-3), message, null, null);
                    if (message == IntPtr.Zero) break;
                    if(!visitedMessages.Add(message)||enumerationClock.ElapsedMilliseconds>=200){enumerationChanged=true;break;}
                    uint pid; uint thread = Native.GetWindowThreadProcessId(message, out pid);
                    if (thread != 0 && found.ContainsKey(pid)) threads[thread] = pid;
                }
                // GUI queues need not have a child/message-only window yet.
                // Discover every GUI thread of the selected process group.
                foreach(uint pid in found.Keys.ToArray())try {
                    using(var process=Process.GetProcessById((int)pid))foreach(ProcessThread thread in process.Threads) {
                        var info=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
                        if(Native.GetGUIThreadInfo((uint)thread.Id,ref info))threads[(uint)thread.Id]=pid;
                    }
                }catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}catch(ArgumentException){}
                uint[] tids = SelectThreads(threads,foregroundThread,focusThread);
                uint[] requestedPids = tids.Select(t => threads[t]).Distinct().OrderBy(p => p).ToArray();
                var processes=ReadProcesses(requestedPids);
                foreach(uint pid in requestedPids) {if(processes.ContainsKey(pid))unavailablePids.Remove(pid);else unavailablePids.Add(pid);}
                var aliveThreads=ReadThreads(tids.Where(t=>processes.ContainsKey(threads[t])).ToDictionary(t=>t,t=>threads[t]));
                tids=tids.Where(aliveThreads.ContainsKey).ToArray();
                uint[] pids = tids.Select(t => threads[t]).Distinct().OrderBy(p => p).ToArray();
                bool foregroundAvailable=processes.ContainsKey(foregroundPid) && tids.Contains(foregroundThread);
                if(!foregroundAvailable)unavailablePids.Add(foregroundPid);
                metadataError=!foregroundAvailable?L.T("当前应用的输入线程暂不可访问"):null;
                metadataWarning=enumerationChanged?L.T("输入窗口正在变化，正在重新检查"):null;
                if(enumerationChanged)nextRefresh=DateTime.UtcNow;
                PublishActivity();
                // A disappearing or inaccessible child must not tear down the
                // subscriptions which are still serving other applications.
                if(tids.Length==0){boundRoot=foreground;return;}
                string next = String.Join(",", pids) + "/" + String.Join(",", tids);
                boundRoot = foreground;
                bool reused=ReusedIdentity(processBirths,pids.ToDictionary(p=>p,p=>processes[p].Created)) || ReusedIdentity(threadBirths,aliveThreads);
                if (signature == next && (bridge != null || companion != null) && HookError == null && !reused) return;
                var bits32 = new HashSet<uint>(pids.Where(p=>processes[p].Bits32));
                uint[] pids64 = pids.Where(p => !bits32.Contains(p)).ToArray();
                uint[] tids64 = tids.Where(t => !bits32.Contains(threads[t])).ToArray();
                uint[] pids32 = pids.Where(p => bits32.Contains(p)).ToArray();
                uint[] tids32 = tids.Where(t => bits32.Contains(threads[t])).ToArray();
                if (HookError != null) {
                    FlushPending();
                    if(companion!=null)SaveEvents(companion.StopAndDrain(),true);
                    Dispose(); boundRoot=foreground;
                    Prewarm(settings,0);
                }
                else if(reused) {
                    // Native Reconcile validates creation identities and stages
                    // replacement slots transactionally; preserve the live
                    // bootstrap generation while saving the old source's tail.
                    FlushPending();
                    foreach(uint pid in pids){ulong old;if(processBirths.TryGetValue(pid,out old)&&old!=processes[pid].Created)nativeNames.Remove(pid);}
                }
                // Keep existing hooks and queues while adding the new editor.
                // Focus changes no longer create an unhook/reinstall gap.
                if (pids64.Length > 0 && tids64.Length > 0) {
                    if(bridge==null) { bridge = new NativeInputBridge(); bridge.StartForThreads(pids64, tids64); }
                    else {bridge.Reconcile(pids64,tids64);flushWarning=bridge.LastFlushMissing==0?null:L.T("输入窗口响应较慢，部分末尾文字尚未确认");}
                    hookedPids64.Clear();hookedThreads64.Clear();hookedPids64.UnionWith(pids64);hookedThreads64.UnionWith(tids64);
                }
                if (pids32.Length > 0 && tids32.Length > 0) {
                    if(companion==null)companion=new X86InputBridge();
                    if(!companion.IsStarted && !bootstrap64)companion.Start(pids32,tids32);
                    else companion.Reconcile(pids32,tids32);
                    hookedPids32.Clear();hookedThreads32.Clear();hookedPids32.UnionWith(pids32);hookedThreads32.UnionWith(tids32);
                }
                foreach (uint pid in pids) sources[pid] = found[pid];
                foreach(uint pid in pids)nativeNames[pid]=foundNames[pid];
                foreach(uint pid in pids)source32[pid]=processes[pid].Bits32;
                foreach(uint pid in pids)processBirths[pid]=processes[pid].Created;
                foreach(uint thread in tids)threadBirths[thread]=aliveThreads[thread];
                signature = next; error = null;
                PublishActivity();
            } catch (Exception e) { FlushPending(); Dispose(); error = L.T("输入监听暂不可用（") + e.GetType().Name + L.T("）"); }
        }
        void SaveFinished(IEnumerable<CommittedInput> events) {
            SaveEvents(events,null);
        }
        void SaveEvents(IEnumerable<CommittedInput> events,bool? bits32) {
            foreach(var input in events) { string source;if(TrySource(input,out source,bits32))finished.Enqueue(new KeyValuePair<CommittedInput,string>(input,source)); }
        }
        bool TrySource(CommittedInput input,out string source,bool? bits32) {
            source=null;
            if(bits32.HasValue)source32[input.ProcessId]=bits32.Value;
            // Kind 6 is native policy-certified process basename metadata,
            // emitted before bootstrap text. It is never an input-history row.
            if(input.Kind==6) {
                if((input.Flags&512)!=0 && !String.IsNullOrWhiteSpace(input.Text)) {
                    nativeNames.Remove(input.ProcessId);sources.Remove(input.ProcessId);
                    string name=input.Text;
                    if(name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))name=name.Substring(0,name.Length-4);
                    if(name.IndexOfAny(new[]{'\\','/',':','\0','\r','\n'})<0 && Allowed(name,exclusions)) {
                        nativeNames[input.ProcessId]=name;sources[input.ProcessId]=SourceName(name);
                    }
                }
                return false;
            }
            string nativeName;
            if(nativeNames.TryGetValue(input.ProcessId,out nativeName)) {
                if(!Allowed(nativeName,exclusions))return false;
                source=SourceName(nativeName);return true;
            }
            if(sources.TryGetValue(input.ProcessId,out source))return true;
            if((input.Flags&512)==0)return false;
            try {
                using(var process=Process.GetProcessById((int)input.ProcessId)) {
                    string name=process.ProcessName;
                    if(!Allowed(name,exclusions))return false;
                    nativeNames[input.ProcessId]=name;sources[input.ProcessId]=source=SourceName(name);return true;
                }
            }catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            return false;
        }
        public void FlushPending() {
            try { if(bridge!=null){SaveEvents(bridge.FinishPending(),false);flushWarning=bridge.LastFlushMissing==0?null:L.T("输入窗口响应较慢，部分末尾文字尚未确认");} }
            catch(Exception e){error=L.T("输入监听暂不可用（")+e.GetType().Name+L.T("）");}
            try { if(companion!=null)SaveEvents(companion.FlushAndDrain(),true); }
            catch(Exception e){error=L.T("输入监听暂不可用（")+e.GetType().Name+L.T("）");}
        }
        public void Drain(Action<CommittedInput, string> receive) {
            while(finished.Count>0) { var item=finished.Dequeue();receive(item.Key,item.Value); }
            // Resolve each queue in delivery order before merging timestamps so
            // a later recycled PID's metadata cannot rename an earlier packet.
            var resolved=new List<KeyValuePair<CommittedInput,string>>();
            Action<IEnumerable<CommittedInput>,bool> resolve=delegate(IEnumerable<CommittedInput> events,bool bits32){foreach(var input in events){string source;if(TrySource(input,out source,bits32))resolved.Add(new KeyValuePair<CommittedInput,string>(input,source));}};
            if(bridge!=null)resolve(bridge.Drain(),false);
            if(companion!=null)resolve(companion.Drain(),true);
            foreach(var item in resolved.OrderBy(e=>e.Key.Tick))receive(item.Key,item.Value);
            // Drop counters describe the lifetime of each queue, not every poll.
            if(lossBridge!=bridge){lossBridge=bridge;observedNativeDropped=0;}
            if(lossCompanion!=companion){lossCompanion=companion;observedCompanionDropped=0;}
            uint nativeDropped=bridge==null?0:bridge.Dropped;
            int companionDropped=companion==null?0:companion.Dropped;
            if(nativeDropped!=observedNativeDropped || companionDropped!=observedCompanionDropped)dataLoss=true;
            observedNativeDropped=nativeDropped;observedCompanionDropped=companionDropped;
        }
        public void Dispose() {
            Interlocked.Exchange(ref activitySinks,new ActivitySink[0]);
            if (bridge != null) { try{SaveEvents(bridge.FinishPending(),false);if(bridge.LastFlushMissing!=0)flushWarning=L.T("输入窗口响应较慢，部分末尾文字尚未确认");}catch{}finally{bridge.Dispose();bridge=null;} }
            if (companion != null) { try{SaveEvents(companion.StopAndDrain(),true);}catch{}finally{companion.Dispose();SaveEvents(companion.Drain(),true);companion=null;} }
            sources.Clear();nativeNames.Clear();source32.Clear(); signature = null; boundRoot = IntPtr.Zero;bootstrap64=false;bootstrapPolicy=null;
            hookedPids64.Clear();hookedPids32.Clear();hookedThreads64.Clear();hookedThreads32.Clear();
            processBirths.Clear();threadBirths.Clear();unavailablePids.Clear();metadataError=metadataWarning=null;
            flushWarning=null;
        }
    }
}
