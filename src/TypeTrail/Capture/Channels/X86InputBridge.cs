using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

namespace TypeTrail {
    // A private child-process pipe carries only committed-character packets.
    internal sealed class X86InputBridge : IDisposable {
        readonly ConcurrentQueue<CommittedInput> events = new ConcurrentQueue<CommittedInput>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
        readonly object sendGate = new object(), stateGate = new object();
        readonly Queue<Outbound> commands = new Queue<Outbound>();
        readonly AutoResetEvent commandReady = new AutoResetEvent(false);
        Outbound pendingActivity;
        sealed class Outbound { public string Text; public bool Metadata, Close; public uint Pid; public ulong Window,At; }
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        Process host;
        StreamWriter input;
        volatile string error;
        volatile string warning;
        volatile bool disposed;
        volatile bool stopQueued;
        bool stopped;
        volatile bool outputEnded;
        volatile bool ready;
        volatile bool nativeStarted, bootstrapReady;
        ActivityChannel activityChannel;
        // Open/map happens on the stdout reader before readiness is published.
        // The hook thread only acquires a reader reference and writes POD.
        sealed class ActivityChannel {
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenFileMapping(uint access,bool inherit,string name);
            [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr MapViewOfFile(IntPtr mapping,uint access,uint high,uint low,UIntPtr size);
            [DllImport("kernel32.dll")]static extern bool UnmapViewOfFile(IntPtr view);
            [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
            IntPtr mapping,view;
            int readers,retired;
            public static ActivityChannel Open(string name,int owner) {
                var channel=new ActivityChannel();
                try {
                    channel.mapping=OpenFileMapping(6,false,name);
                    if(channel.mapping==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                    channel.view=MapViewOfFile(channel.mapping,6,0,0,new UIntPtr(96));
                    if(channel.view==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                    if(Marshal.ReadInt32(channel.view)!=unchecked((int)0x54544143) || Marshal.ReadInt32(channel.view,4)!=1 || Marshal.ReadInt32(channel.view,8)!=96 || Marshal.ReadInt32(channel.view,12)!=owner || Marshal.ReadInt32(channel.view,16)==0 || Marshal.ReadInt32(channel.view,24)==0)throw new InvalidDataException("Invalid native activity channel");
                    return channel;
                }catch{channel.Close();throw;}
            }
            public bool Write(uint pid,ulong window,ulong tick) {
                Interlocked.Increment(ref readers);
                try {return Interlocked.CompareExchange(ref retired,0,0)==0 && NativeInputBridge.WriteActivity(view,pid,window,tick);}
                finally {Interlocked.Decrement(ref readers);}
            }
            void Close(){if(view!=IntPtr.Zero){UnmapViewOfFile(view);view=IntPtr.Zero;}if(mapping!=IntPtr.Zero){CloseHandle(mapping);mapping=IntPtr.Zero;}}
            public void Retire() {
                if(Interlocked.Exchange(ref retired,1)!=0)return;
                // A suspended hook callback may still own this mapping. Never
                // wait for it or unmap beneath it on either UI or hook thread.
                ThreadPool.QueueUserWorkItem(delegate {while(Interlocked.CompareExchange(ref readers,0,0)!=0)Thread.Sleep(1);Close();});
            }
        }
        uint[] readyPids = new uint[0];
        public bool Ready { get { return ready && !disposed && !outputEnded; } }
        public bool IsStarted { get { return nativeStarted && !disposed && !stopQueued && !outputEnded; } }
        public bool BootstrapReady {get{return bootstrapReady && IsStarted;}}
        public bool IsTarget(uint pid) { return (bootstrapReady || Array.IndexOf(Interlocked.CompareExchange(ref readyPids, null, null), pid) >= 0) && IsStarted; }
        long stopStarted;
        long nextFlush, acknowledgedFlush;
        DateTime lastStats;
        public string Error { get { return error; } }
        public string Warning { get { return warning; } }
        int dropped;
        uint observedNativeDropped;
        public int Dropped { get { return Interlocked.CompareExchange(ref dropped,0,0); } }
        sealed class Packet {
            public string Type { get; set; }
            public string Error { get; set; }
            public string Message { get; set; }
            public int Code { get; set; }
            public string Text { get; set; }
            public uint Kind { get; set; }
            public uint ProcessId { get; set; }
            public uint ThreadId { get; set; }
            public uint Flags { get; set; }
            public ulong Window { get; set; }
            public ulong Tick { get; set; }
            public uint Dropped { get; set; }
            public long Id { get; set; }
            public uint Missing { get; set; }
            public uint InputSerial { get; set; }
            public uint[] Pids { get; set; }
            public string ActivityChannel {get;set;}
            public uint ActivityProtocol {get;set;}
        }
        public bool Warm(int timeoutMilliseconds = 0) {
            if(host==null) Launch();
            var clock=Stopwatch.StartNew();
            lock(stateGate) {
                while(!ready && !outputEnded && error==null) {
                    int remaining=Math.Max(0,timeoutMilliseconds)-(int)clock.ElapsedMilliseconds;
                    if(remaining<=0)break;
                    Monitor.Wait(stateGate,remaining);
                }
            }
            return Ready;
        }
        public void Start(uint[] pids, uint[] threads) {
            if(host==null) Launch();
            if(disposed || stopQueued || IsStarted)throw new InvalidOperationException("32-bit input helper has already been started");
            Send(new { Op = "start", Pids = pids, Threads = threads });
        }
        void Launch() {
            if (disposed || host != null) throw new InvalidOperationException("32-bit input helper has already been started");
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TypeTrail.InputHost.x86.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("32-bit input helper is missing", path);
            host = new Process { StartInfo = new ProcessStartInfo(path, "--parent " + Process.GetCurrentProcess().Id) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            }};
            host.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                // Output already queued by the child must remain readable while
                // stopping. The stopped acknowledgement is behind every final
                // event on this same pipe, so it forms an ordered drain barrier.
                if (args.Data == null) {
                    lock (stateGate) { outputEnded = true; Monitor.PulseAll(stateGate); }
                    return;
                }
                try {
                    var packet = new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue}.Deserialize<Packet>(args.Data);
                    if(packet.Type=="ready") {lock(stateGate){ready=true;Monitor.PulseAll(stateGate);}}
                    if(packet.Type=="bootstrapped") {
                        ActivityChannel mapped=null;
                        if(packet.ActivityProtocol!=0) {
                            if(packet.ActivityProtocol!=1 || String.IsNullOrWhiteSpace(packet.ActivityChannel))throw new InvalidDataException("Missing native activity channel");
                            mapped=ActivityChannel.Open(packet.ActivityChannel,host.Id);
                        }
                        if(disposed||stopQueued){if(mapped!=null)mapped.Retire();}
                        else {
                            var previous=Interlocked.Exchange(ref activityChannel,mapped);if(previous!=null)previous.Retire();
                            lock(stateGate){nativeStarted=bootstrapReady=true;Monitor.PulseAll(stateGate);}
                        }
                    }
                    if(packet.Type=="started" || packet.Type=="reconciled") {
                        nativeStarted=true;
                        Interlocked.Exchange(ref readyPids,packet.Pids??new uint[0]);
                        warning=packet.Missing==0?null:L.T("输入窗口响应较慢，部分末尾文字尚未确认");
                    }
                    if(packet.Type=="extended") {
                        var combined=new HashSet<uint>(Interlocked.CompareExchange(ref readyPids,null,null));
                        foreach(uint pid in packet.Pids??new uint[0])combined.Add(pid);
                        var snapshot=new uint[combined.Count];combined.CopyTo(snapshot);Interlocked.Exchange(ref readyPids,snapshot);
                    }
                    if (packet.Type == "error") {
                        if(packet.Error=="NativeFlushIncomplete")warning=L.T("输入窗口响应较慢，部分末尾文字尚未确认");
                        else error = packet.Error+(packet.Code==0?"":" ("+packet.Code+")")+(String.IsNullOrEmpty(packet.Message)?"":": "+packet.Message);
                    }
                    if (packet.Type == "diagnostics") {
                        if(packet.Dropped!=0 && packet.Dropped!=observedNativeDropped)Interlocked.Increment(ref dropped);
                        observedNativeDropped=packet.Dropped;
                        if(packet.Dropped!=0)error=L.T("输入过快，部分字符未能保存");
                    }
                    if (packet.Type == "event") {
                        if (events.Count >= 4096) { Interlocked.Increment(ref dropped); error = "32-bit input queue overflow"; return; }
                        events.Enqueue(new CommittedInput { Kind = packet.Kind, ProcessId = packet.ProcessId, ThreadId = packet.ThreadId, Flags = packet.Flags, InputSerial=packet.InputSerial, Window = packet.Window, Tick = packet.Tick, Text = packet.Text });
                    }
                    if (packet.Type == "stopped") {
                        var previous=Interlocked.Exchange(ref activityChannel,null);if(previous!=null)previous.Retire();
                        nativeStarted=bootstrapReady=false;
                        Interlocked.Exchange(ref readyPids,new uint[0]);
                        warning=packet.Missing==0?null:L.T("输入窗口响应较慢，部分末尾文字尚未确认");
                        lock (stateGate) { stopped = true; Monitor.PulseAll(stateGate); }
                    }
                    if (packet.Type == "flushed") {
                        warning=packet.Missing==0?null:L.T("输入窗口响应较慢，部分末尾文字尚未确认");
                        lock (stateGate) { acknowledgedFlush = Math.Max(acknowledgedFlush, packet.Id); Monitor.PulseAll(stateGate); }
                    }
                } catch { error = "32-bit input packet unavailable"; }
            };
            host.Start();
            input = new StreamWriter(host.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            host.BeginOutputReadLine(); host.BeginErrorReadLine();
            new Thread(WriteCommands) { IsBackground = true, Name = "TypeTrail x86 command writer" }.Start();
        }
        void Send(object command) {
            SendCore(command, false);
        }
        bool SendCore(object command, bool shutdownCommand, bool metadata = false, uint pid = 0, ulong window = 0, bool close = false,ulong at=0) {
            try {
                lock (sendGate) {
                    if (disposed || input == null || (stopQueued && !shutdownCommand)) return false;
                    string text = json.Serialize(command);
                    if (metadata && pendingActivity != null && pendingActivity.Pid == pid && pendingActivity.Window == window) {
                        if(at>=pendingActivity.At){pendingActivity.Text = text;pendingActivity.At=at;}
                        return true;
                    }
                    if (commands.Count >= 512) {
                        if (metadata) { Interlocked.Increment(ref dropped); if (error == null) error = "32-bit input activity queue overflow"; return false; }
                        // Keep configuration/barrier ordering while shedding only
                        // redundant keyboard-activity metadata under backpressure.
                        var controls = new List<Outbound>();
                        while (commands.Count > 0) { var queued = commands.Dequeue(); if (!queued.Metadata) controls.Add(queued); }
                        pendingActivity = null;
                        foreach (var queued in controls) commands.Enqueue(queued);
                        if (commands.Count >= 512 && !shutdownCommand) { Interlocked.Increment(ref dropped); if (error == null) error = "32-bit input command queue overflow"; return false; }
                    }
                    var outgoing = new Outbound { Text = text, Metadata = metadata, Pid = pid, Window = window, Close = close,At=at };
                    commands.Enqueue(outgoing);
                    pendingActivity = metadata ? outgoing : null;
                }
                commandReady.Set();
                return true;
            } catch { if (error == null) error = "32-bit input helper disconnected";return false; }
        }
        void WriteCommands() {
            try {
                for (;;) {
                    Outbound command = null;
                    lock (sendGate) {
                        if (commands.Count > 0) {
                            command = commands.Dequeue();
                            if (pendingActivity == command) pendingActivity = null;
                        }
                    }
                    if (command == null) { commandReady.WaitOne(); continue; }
                    // Pipe writes run only here. A stalled helper can no longer
                    // block the global keyboard hook or the controller UI.
                    input.WriteLine(command.Text);
                    if (command.Close) return;
                }
            } catch { if (error == null) error = "32-bit input helper disconnected"; }
            finally { try { if (input != null) input.Close(); } catch { } }
        }
        public void Extend(uint[] pids, uint[] threads) { Send(new { Op = "extend", Pids = pids, Threads = threads }); }
        public void Reconcile(uint[] pids,uint[] threads) { Send(new {Op="reconcile",Pids=pids,Threads=threads}); }
        public bool Bootstrap(string exclusions,int timeoutMilliseconds=0) {
            if(host==null)Launch();
            if(!bootstrapReady)Send(new {Op="bootstrap",Exclusions=exclusions??""});
            else SetBootstrapPolicy(exclusions);
            var clock=Stopwatch.StartNew();
            lock(stateGate)while(!bootstrapReady && !outputEnded && error==null) {
                int remaining=Math.Max(0,timeoutMilliseconds)-(int)clock.ElapsedMilliseconds;
                if(remaining<=0)break;
                Monitor.Wait(stateGate,remaining);
            }
            return BootstrapReady;
        }
        public void SetBootstrapPolicy(string exclusions) {Send(new {Op="policy",Exclusions=exclusions??""});}
        public void NotifyInput(uint pid, ulong window) { NotifyInputAt(pid,window,GetTickCount64()); }
        public bool NotifyInputAt(uint pid,ulong window,ulong tick) {
            if(disposed||stopQueued||outputEnded)return false;
            var mapped=Interlocked.CompareExchange(ref activityChannel,null,null);
            if(mapped!=null)try{return mapped.Write(pid,window,tick);}catch{warning=L.T("32位输入同步通道暂不可用");return false;}
            return SendCore(new { Op = "input", Pid = pid, Window = window, At = tick }, false, true, pid, window,false,tick);
        }
        public List<CommittedInput> Drain() {
            if ((DateTime.UtcNow-lastStats).TotalSeconds >= 1) { lastStats=DateTime.UtcNow; Send(new { Op="stats" }); }
            var result = new List<CommittedInput>(); CommittedInput item;
            while (events.TryDequeue(out item)) result.Add(item);
            if (host != null && host.HasExited && !disposed) error = "32-bit input helper stopped";
            return result;
        }
        void WaitForStop(int timeoutMilliseconds) {
            bool queue = false;
            lock (stateGate) {
                if (host == null || disposed || stopped) return;
                if (!stopQueued) { stopStarted = Stopwatch.GetTimestamp(); stopQueued = true; queue = true; }
            }
            if (queue) SendCore(new { Op = "stop" }, true);
            var elapsed = Stopwatch.StartNew();
            lock (stateGate) {
                while (!stopped && !outputEnded) {
                    int remaining = Math.Min(timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds, ShutdownBudgetRemaining());
                    if (remaining <= 0) break;
                    Monitor.Wait(stateGate, remaining);
                }
                if (!stopped) error = outputEnded ? "32-bit input helper ended before confirming its final input" : "32-bit input helper final input timed out";
            }
        }
        int ShutdownBudgetRemaining() {
            if (stopStarted == 0) return 600;
            long age = (Stopwatch.GetTimestamp() - stopStarted) * 1000 / Stopwatch.Frequency;
            return (int)Math.Max(0, 600 - age);
        }
        public List<CommittedInput> StopAndDrain(int timeoutMilliseconds = 600) {
            WaitForStop(Math.Max(0, Math.Min(600, timeoutMilliseconds)));
            var result = new List<CommittedInput>(); CommittedInput item;
            while (events.TryDequeue(out item)) result.Add(item);
            return result;
        }
        public List<CommittedInput> FlushAndDrain(int timeoutMilliseconds = 400) {
            if (stopQueued) return StopAndDrain(Math.Max(0, Math.Min(400, timeoutMilliseconds)));
            if (disposed || host == null) return Drain();
            long request = Interlocked.Increment(ref nextFlush);
            var elapsed = Stopwatch.StartNew();
            Send(new { Op = "flush", Id = request });
            lock (stateGate) {
                while (acknowledgedFlush < request && !outputEnded) {
                    int remaining = Math.Max(0, Math.Min(400, timeoutMilliseconds)) - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) break;
                    Monitor.Wait(stateGate, remaining);
                }
                if (acknowledgedFlush < request) error = outputEnded ? "32-bit input helper ended before flushing pending input" : "32-bit input helper pending input timed out";
            }
            var result = new List<CommittedInput>(); CommittedInput item;
            while (events.TryDequeue(out item)) result.Add(item);
            return result;
        }
        public void Dispose() {
            if (disposed) return;
            var mapped=Interlocked.Exchange(ref activityChannel,null);if(mapped!=null)mapped.Retire();
            var elapsed = Stopwatch.StartNew();
            WaitForStop(600);
            Process closing = host;
            if (closing != null) {
                // Preserve event reception until the stop barrier has passed.
                // Do not consume the queue here: Drain remains available after
                // disposal, including for callers replacing a foreground target.
                SendCore(new { Op = "exit" }, true, false, 0, 0, true);
                try {
                    int remaining = Math.Min(Math.Max(0, 600 - (int)elapsed.ElapsedMilliseconds), ShutdownBudgetRemaining());
                    if (!closing.HasExited && !closing.WaitForExit(remaining)) closing.Kill();
                } catch { }
                closing.Dispose(); host = null;
            }
            disposed = true;
        }
    }
}
