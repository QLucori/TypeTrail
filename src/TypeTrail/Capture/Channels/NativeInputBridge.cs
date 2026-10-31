using System;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeTrail {
    public sealed class CommittedInput {
        public uint Kind, ProcessId, ThreadId, Flags, InputSerial;
        public ulong Window, Tick;
        public ulong OriginTick;
        public string Text;
        public string FieldIdentity;
    }
    // Call lifecycle/Drain on the controller's single dispatcher thread.
    // The DLL must be alongside TypeTrail.exe; this wrapper is .NET 4.8 / C# 5.
    public sealed class NativeInputBridge : IDisposable {
        const int Capacity = 2048, EventSize = 552;
        const string LibraryName = "TypeTrail.InputBridge.1.7.13.r6.dll";
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Start(uint threadId, uint[] targets, uint count, [MarshalAs(UnmanagedType.Bool)] bool testOwner);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_StartThreads(uint[] targets,uint count,uint[] threadIds,uint threadCount,[MarshalAs(UnmanagedType.Bool)] bool testOwner);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Extend(uint[] targets,uint count,uint[] threadIds,uint threadCount);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Reconcile(uint[] targets,uint count,uint[] threadIds,uint threadCount);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Flush(uint timeoutMilliseconds);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern void TT_Stop();
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Read(IntPtr buffer, uint count);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Dropped();
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_Stats([Out] uint[] values,uint capacity);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TT_NotifyInput(uint pid,ulong window);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] static extern uint TT_LastRetireMissing();
        public void Reconcile(uint[] pids,uint[] threads) { if(!active)throw new InvalidOperationException("Bridge is not running");uint error=TT_Reconcile(pids,(uint)pids.Length,threads,(uint)threads.Length);if(error!=0)throw new Win32Exception((int)error);LastFlushMissing=TT_LastRetireMissing(); }
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TT_NotifyInputAt(uint pid,ulong window,ulong tick);
        public bool NotifyInputAt(uint pid,ulong window,ulong tick) { return active && TT_NotifyInputAt(pid,window,tick); }
        [DllImport(LibraryName, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_StartBootstrap(string exclusions,uint chars);
        [DllImport(LibraryName, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_SetBootstrapPolicy(string exclusions,uint chars);
        [DllImport(LibraryName, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)] static extern uint TT_GetActivityChannelName(StringBuilder output,uint capacity);
        [DllImport(LibraryName, CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool TT_WriteActivity(IntPtr mapped,uint pid,ulong window,ulong tick);
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
        CommittedInput high;
        public uint Dropped { get { return active ? TT_Dropped() : 0; } }
        public uint LastFlushMissing { get; private set; }
        public uint[] Diagnostics { get { var values=new uint[41];if(active)TT_Stats(values,41);return values; } }
        public bool NotifyInput(uint processId,ulong window) { return active && TT_NotifyInput(processId,window); }
        public void Extend(uint[] pids,uint[] threads) {
            if(!active)throw new InvalidOperationException("Bridge is not started");
            uint error=TT_Extend(pids,(uint)pids.Length,threads,(uint)threads.Length);
            if(error!=0)throw new Win32Exception((int)error);
        }
        public List<CommittedInput> FinishPending() { LastFlushMissing=active?TT_Flush(150):0;return Drain(); }
        public void Start(uint[] allowedProcessIds) { Start(allowedProcessIds, 0, false); }
        public void StartForThreads(uint[] allowedProcessIds,uint[] selectedGuiThreads) {
            if(active)throw new InvalidOperationException("Already started");
            if(allowedProcessIds==null || allowedProcessIds.Length==0 || selectedGuiThreads==null || selectedGuiThreads.Length==0)throw new ArgumentException("Supply explicit process IDs and GUI thread IDs");
            buffer=Marshal.AllocHGlobal(Capacity*EventSize);
            uint error=TT_StartThreads(allowedProcessIds,(uint)allowedProcessIds.Length,selectedGuiThreads,(uint)selectedGuiThreads.Length,false);
            if(error!=0){Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;throw new Win32Exception((int)error);}
            active=true;
        }
        public void Start(uint[] allowedProcessIds, uint ownTestThread, bool includeOwnerForTest) {
            if (active) throw new InvalidOperationException("Already started");
            if (allowedProcessIds == null || allowedProcessIds.Length == 0) throw new ArgumentException("Supply explicitly selected process IDs");
            buffer = Marshal.AllocHGlobal(Capacity * EventSize);
            uint error = TT_Start(ownTestThread, allowedProcessIds, (uint)allowedProcessIds.Length, includeOwnerForTest);
            if (error != 0) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; throw new Win32Exception((int)error); }
            active = true;
        }
        static bool SameSource(CommittedInput a, CommittedInput b) { return a.ProcessId == b.ProcessId && a.ThreadId == b.ThreadId && a.Window == b.Window; }
        public List<CommittedInput> Drain() {
            var result = new List<CommittedInput>(); if (!active) return result;
            uint count = TT_Read(buffer, Capacity);
            for (int i = 0; i < count; ++i) {
                IntPtr p = IntPtr.Add(buffer, i * EventSize);
                uint length = unchecked((uint)Marshal.ReadInt32(p, 12));
                if (length > 256) continue;
                var item = new CommittedInput { Kind = unchecked((uint)Marshal.ReadInt32(p)), ProcessId = unchecked((uint)Marshal.ReadInt32(p, 4)), ThreadId = unchecked((uint)Marshal.ReadInt32(p, 8)), Window = unchecked((ulong)Marshal.ReadInt64(p, 16)), Tick = unchecked((ulong)Marshal.ReadInt64(p, 24)), Text = length == 0 ? "" : Marshal.PtrToStringUni(IntPtr.Add(p, 32), (int)length) };
                item.Flags=unchecked((uint)Marshal.ReadInt32(p,544));item.InputSerial=unchecked((uint)Marshal.ReadInt32(p,548));
                if (high != null) {
                    if (item.Kind == 1 && SameSource(high, item) && item.Text.Length > 0 && Char.IsLowSurrogate(item.Text[0])) item.Text = high.Text + item.Text;
                    high = null;
                }
                if (item.Kind == 1 && item.Text.Length > 0 && Char.IsHighSurrogate(item.Text[item.Text.Length - 1])) {
                    high = new CommittedInput { Kind = item.Kind, ProcessId = item.ProcessId, ThreadId = item.ThreadId, Window = item.Window, Tick = item.Tick, Text = item.Text.Substring(item.Text.Length - 1) };
                    item.Text = item.Text.Substring(0, item.Text.Length - 1);
                }
                if (item.Kind != 1 || item.Text.Length > 0) result.Add(item);
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
