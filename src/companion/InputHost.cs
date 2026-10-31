using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace TypeTrail.Companion {
    internal sealed class Command {
        public string Op;
        public uint[] Pids, Threads;
        public uint Pid;
        public ulong Window;
        public long Id;
        public ulong At;
        public string Exclusions;
    }

    internal sealed class HostContext : ApplicationContext {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        readonly Queue<Command> commands = new Queue<Command>();
        Command pendingActivity;
        bool activityQueueOverflow;
        readonly System.Windows.Forms.Timer timer;
        readonly int parent;
        IntPtr parentHandle;
        NativeBridge bridge;
        bool stopping;
        DateTime checkedParent;
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public HostContext(int parentPid) {
            parent = parentPid;
            if(parent>0){parentHandle=OpenProcess(0x100000,false,parent);if(parentHandle==IntPtr.Zero)throw new InvalidOperationException("The input helper parent is not running");}
            timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += Tick;
            var reader = new Thread(ReadCommands) { IsBackground = true, Name = "TypeTrail x86 command reader" };
            reader.Start();
            Send(new { Type = "ready", Bits = IntPtr.Size * 8 });
            timer.Start();
        }
        void ReadCommands() {
            try {
                var decoder = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                string line;
                while ((line = Console.ReadLine()) != null) {
                    Command command;
                    try { command = decoder.Deserialize<Command>(line); }
                    catch { command = new Command { Op = "invalid" }; }
                    lock (commands) {
                        if (command != null && command.Op == "input" && pendingActivity != null && pendingActivity.Pid == command.Pid && pendingActivity.Window == command.Window) {
                            if(command.At>=pendingActivity.At)pendingActivity.At = command.At;
                            continue;
                        }
                        if (commands.Count >= 1024) {
                            if (command != null && command.Op == "input") { activityQueueOverflow = true; continue; }
                            // Activity metadata may be coalesced; configuration,
                            // stop and flush barriers must keep their FIFO order.
                            var controls = new List<Command>();
                            while (commands.Count > 0) { var queued = commands.Dequeue(); if (queued == null || queued.Op != "input") controls.Add(queued); }
                            pendingActivity = null;
                            foreach (var queued in controls) commands.Enqueue(queued);
                            if (commands.Count >= 1024) { commands.Clear(); commands.Enqueue(new Command { Op = "exit" }); return; }
                        }
                        commands.Enqueue(command);
                        pendingActivity = command != null && command.Op == "input" ? command : null;
                    }
                }
            } catch { }
            lock (commands) commands.Enqueue(new Command { Op = "exit" });
        }
        void Send(object value) {
            try { Console.WriteLine(json.Serialize(value)); Console.Out.Flush(); }
            catch (IOException) { StopHost(); }
        }
        void Drain() { if (bridge != null) foreach (var input in bridge.Drain()) Send(input); }
        uint SendPending(NativeBridge current) {
            foreach (var input in current.FinishPending()) Send(input);
            if (current.LastFlushMissing != 0) Send(new { Type = "error", Error = "NativeFlushIncomplete" });
            return current.LastFlushMissing;
        }
        uint StopBridge() {
            if (bridge == null) return 0;
            NativeBridge closing = bridge;
            bridge = null;
            try { return SendPending(closing); }
            finally { closing.Dispose(); }
        }
        void StopHost() {
            if (stopping) return;
            stopping = true;
            timer.Stop();
            try {StopBridge();}
            finally {
                if(parentHandle!=IntPtr.Zero){CloseHandle(parentHandle);parentHandle=IntPtr.Zero;}
                timer.Dispose();ExitThread();
            }
        }
        void Tick(object sender, EventArgs args) {
            if (stopping) return;
            try {
                if (parent > 0 && (DateTime.UtcNow - checkedParent).TotalSeconds >= 1) {
                    checkedParent = DateTime.UtcNow;
                    // Retain the original process object identity: a recycled
                    // numeric PID must never keep an orphaned helper alive.
                    if(parentHandle==IntPtr.Zero || WaitForSingleObject(parentHandle,0)!=258){StopHost();return;}
                }
                for (int i = 0; i < 256 && !stopping; i++) {
                    Command command; bool overflow;
                    lock (commands) {
                        if (commands.Count == 0) break;
                        command = commands.Dequeue();
                        if (pendingActivity == command) pendingActivity = null;
                        overflow = activityQueueOverflow; activityQueueOverflow = false;
                    }
                    if (overflow) Send(new { Type = "error", Error = "InputActivityQueueOverflow" });
                    try { Handle(command); }
                    catch (Exception error) { Send(new { Type = "error", Error = error.GetType().Name, Message = error.Message, Code = error is System.ComponentModel.Win32Exception ? ((System.ComponentModel.Win32Exception)error).NativeErrorCode : 0 }); }
                }
                if (!stopping) Drain();
            } catch (Exception error) { Send(new { Type = "error", Error = error.GetType().Name, Message = error.Message }); }
        }
        void Handle(Command command) {
            if (command == null || command.Op == null) throw new ArgumentException("Missing Op");
            switch (command.Op) {
                case "bootstrap":
                    if(bridge==null) {
                        var warm=new NativeBridge();
                        try{warm.StartBootstrap(command.Exclusions??"");bridge=warm;}
                        catch{warm.Dispose();throw;}
                    }else if(!bridge.BootstrapReady)bridge.StartBootstrap(command.Exclusions??"");
                    else bridge.SetBootstrapPolicy(command.Exclusions??"");
                    Send(new {Type="bootstrapped",ActivityProtocol=1u,ActivityChannel=bridge.ActivityChannelName});
                    break;
                case "policy":
                    if(bridge==null)throw new InvalidOperationException("Bridge is not running");
                    bridge.SetBootstrapPolicy(command.Exclusions??"");
                    Send(new {Type="policyUpdated"});
                    break;
                case "start":
                    StopBridge();
                    var next = new NativeBridge();
                    try { next.Start(command.Pids, command.Threads); bridge = next; }
                    catch { next.Dispose(); throw; }
                    Send(new { Type = "started", Pids = command.Pids, Threads = command.Threads });
                    break;
                case "input":
                    if(bridge!=null) {
                        if(command.At==0)bridge.Notify(command.Pid,command.Window);
                        else bridge.NotifyInputAt(command.Pid,command.Window,command.At);
                    }
                    break;
                case "extend":
                    if (bridge == null) throw new InvalidOperationException("Bridge is not running");
                    bridge.Extend(command.Pids, command.Threads);
                    Send(new { Type = "extended", Pids = command.Pids, Threads = command.Threads });
                    break;
                case "reconcile":
                    if(bridge==null)throw new InvalidOperationException("Bridge is not running");
                    bridge.Reconcile(command.Pids,command.Threads);
                    if(bridge.LastFlushMissing!=0)Send(new {Type="error",Error="NativeFlushIncomplete"});
                    Send(new {Type="reconciled",Pids=command.Pids,Threads=command.Threads,Missing=bridge.LastFlushMissing});
                    break;
                case "stats": Send(new { Type = "diagnostics", Values = bridge == null ? new uint[0] : bridge.Diagnostics, Dropped = bridge == null ? 0u : bridge.Dropped }); break;
                case "flush":
                    uint missing = bridge == null ? 0 : SendPending(bridge);
                    Send(new { Type = "flushed", Id = command.Id, Missing = missing });
                    break;
                case "stop": uint stoppedMissing=StopBridge(); Send(new { Type = "stopped", Missing = stoppedMissing }); break;
                case "exit": StopHost(); break;
                default: throw new ArgumentException("Unknown Op");
            }
        }
        protected override void Dispose(bool disposing) {
            if (disposing && !stopping) StopHost();
            base.Dispose(disposing);
        }
    }

    internal static class InputHost {
        [STAThread]
        static int Main(string[] args) {
            int parent = 0;
            if (args.Length == 2 && args[0] == "--parent") {
                if (!Int32.TryParse(args[1], out parent) || parent <= 0) return 2;
            } else if (args.Length != 0) return 2;
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            try { using (var host = new HostContext(parent)) Application.Run(host); return 0; }
            catch { return 1; }
        }
    }
}
