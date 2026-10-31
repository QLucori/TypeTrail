using System;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeTrail.Companion {
    internal sealed class InputEvent {
        public string Type = "event";
        public uint Kind, ProcessId, ThreadId, Flags, InputSerial;
        public ulong Window, Tick;
        public string Text;
    }

    // All native bridge calls and disposal stay on the host's STA message-pump thread.
    internal sealed class NativeBridge : IDisposable {
        const string Library = "TypeTrail.InputBridge.1.7.13.r6.x86.dll";
        const int Capacity = 2048, EventSize = 552;
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern uint TT_StartThreads(uint[] targets, uint count, uint[] threadIds, uint threadCount, [MarshalAs(UnmanagedType.Bool)] bool testOwner);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern uint TT_Extend(uint[] targets, uint count, uint[] threadIds, uint threadCount);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Reconcile(uint[] targets,uint count,uint[] threadIds,uint threadCount);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void TT_Stop();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Read(IntPtr buffer, uint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Dropped();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Stats([Out] uint[] values, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Flush(uint timeoutMilliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool TT_NotifyInput(uint pid, ulong window);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_LastRetireMissing();
        public void Reconcile(uint[] pids,uint[] threads) { if(!active)throw new InvalidOperationException("Bridge is not running");uint error=TT_Reconcile(pids,(uint)pids.Length,threads,(uint)threads.Length);if(error!=0)throw new Win32Exception((int)error);LastFlushMissing=TT_LastRetireMissing(); }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TT_NotifyInputAt(uint pid,ulong window,ulong tick);
        public bool NotifyInputAt(uint pid,ulong window,ulong tick) { return active && TT_NotifyInputAt(pid,window,tick); }
        [DllImport(Library, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_StartBootstrap(string exclusions,uint chars);
        [DllImport(Library, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_SetBootstrapPolicy(string exclusions,uint chars);
        [DllImport(Library, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_GetActivityChannelName(StringBuilder output,uint capacity);
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TT_WriteActivity(IntPtr mapped,uint pid,ulong window,ulong tick);
        public static bool WriteActivity(IntPtr mapped,uint pid,ulong window,ulong tick) { return mapped!=IntPtr.Zero && TT_WriteActivity(mapped,pid,window,tick); }
        public string ActivityChannelName { get { var value=new StringBuilder(256);uint error=TT_GetActivityChannelName(value,256);if(error!=0)throw new Win32Exception((int)error);return value.ToString(); } }
        public bool BootstrapReady { get; private set; }
        public void StartBootstrap(string exclusions) {
            bool opened=!active;
            if(opened)buffer=Marshal.AllocHGlobal(Capacity*EventSize);
            string policy=(exclusions??"")+"\0";
            uint error=TT_StartBootstrap(policy,(uint)policy.Length);
            if(error!=0) {
                if(opened){TT_Stop();Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;}
                throw new Win32Exception((int)error);
            }
            active=true;BootstrapReady=true;
        }
        public void SetBootstrapPolicy(string exclusions) {
            if(!active)throw new InvalidOperationException("Bridge is not running");
            string policy=(exclusions??"")+"\0";
            uint error=TT_SetBootstrapPolicy(policy,(uint)policy.Length);
            if(error!=0)throw new Win32Exception((int)error);
        }
        IntPtr buffer;
        bool active;
        InputEvent high;
        public uint Dropped { get { return active ? TT_Dropped() : 0; } }
        public uint LastFlushMissing { get; private set; }
        public uint[] Diagnostics { get { var values = new uint[25]; if (active) TT_Stats(values, 25); return values; } }
        public void Start(uint[] pids, uint[] threads) {
            if (active) throw new InvalidOperationException("Bridge is already running");
            if (pids == null || pids.Length == 0 || threads == null || threads.Length == 0)
                throw new ArgumentException("Supply explicit process IDs and GUI thread IDs");
            buffer = Marshal.AllocHGlobal(Capacity * EventSize);
            uint error = TT_StartThreads(pids, (uint)pids.Length, threads, (uint)threads.Length, false);
            if (error != 0) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; throw new Win32Exception((int)error); }
            active = true;
        }
        public bool Notify(uint pid, ulong window) { return active && TT_NotifyInput(pid, window); }
        public void Extend(uint[] pids, uint[] threads) {
            if (!active) throw new InvalidOperationException("Bridge is not running");
            if (pids == null || pids.Length == 0 || threads == null || threads.Length == 0)
                throw new ArgumentException("Supply explicit process IDs and GUI thread IDs");
            uint error = TT_Extend(pids, (uint)pids.Length, threads, (uint)threads.Length);
            if (error != 0) throw new Win32Exception((int)error);
        }
        public List<InputEvent> FinishPending() {
            LastFlushMissing = active ? TT_Flush(150) : 0;
            if (!active) return new List<InputEvent>();
            return Drain();
        }
        static bool SameSource(InputEvent a, InputEvent b) {
            return a.ProcessId == b.ProcessId && a.ThreadId == b.ThreadId && a.Window == b.Window;
        }
        public List<InputEvent> Drain() {
            var result = new List<InputEvent>();
            if (!active) return result;
            uint count = TT_Read(buffer, Capacity);
            for (int i = 0; i < count; i++) {
                IntPtr p = IntPtr.Add(buffer, i * EventSize);
                uint length = unchecked((uint)Marshal.ReadInt32(p, 12));
                if (length > 256) continue;
                var item = new InputEvent {
                    Kind = unchecked((uint)Marshal.ReadInt32(p)), ProcessId = unchecked((uint)Marshal.ReadInt32(p, 4)),
                    ThreadId = unchecked((uint)Marshal.ReadInt32(p, 8)), Window = unchecked((ulong)Marshal.ReadInt64(p, 16)),
                    Tick = unchecked((ulong)Marshal.ReadInt64(p, 24)), Flags = unchecked((uint)Marshal.ReadInt32(p, 544)), InputSerial = unchecked((uint)Marshal.ReadInt32(p, 548)),
                    Text = length == 0 ? "" : Marshal.PtrToStringUni(IntPtr.Add(p, 32), (int)length)
                };
                if (high != null) {
                    if (item.Kind == 1 && SameSource(high, item) && item.Text.Length > 0 && Char.IsLowSurrogate(item.Text[0])) item.Text = high.Text + item.Text;
                    high = null;
                }
                if (item.Kind == 1 && item.Text.Length > 0 && Char.IsHighSurrogate(item.Text[item.Text.Length - 1])) {
                    high = new InputEvent { Kind = item.Kind, ProcessId = item.ProcessId, ThreadId = item.ThreadId, Window = item.Window, Tick = item.Tick, Text = item.Text.Substring(item.Text.Length - 1) };
                    item.Text = item.Text.Substring(0, item.Text.Length - 1);
                }
                if (item.Kind != 1 || item.Text.Length != 0) result.Add(item);
            }
            return result;
        }
        public void Dispose() {
            if (active) { TT_Stop(); active = false; }
            high = null;BootstrapReady=false;
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
        }
    }
}
