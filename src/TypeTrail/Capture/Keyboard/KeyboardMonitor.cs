using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TypeTrail {
    internal sealed class KeyboardInput {
        public int Key, Scan;
        public uint Flags, ProcessId, ThreadId, InputProcessId;
        public IntPtr Window, Focus, Layout;
        public ulong Tick;
        public long Timestamp;
        public bool Ctrl, Alt, Shift, Win, AltGr, Caps, Num, Scroll;
        public bool ActivityPulsed;
        public bool FocusEvent;
        public uint FocusKind;
        public int ObjectId,ChildId;
        internal int Generation;
        public byte[] KeyState() {
            var state=new byte[256];
            if(Key>=0 && Key<256)state[Key]=0x80;
            if(Ctrl)state[0x11]=0x80;
            if(Alt)state[0x12]=0x80;
            if(Shift)state[0x10]=0x80;
            if(Win)state[0x5B]=0x80;
            if(AltGr)state[0xA5]=0x80;
            state[0x14]=(byte)(Caps?1:0);state[0x90]=(byte)(Num?1:0);state[0x91]=(byte)(Scroll?1:0);
            return state;
        }
    }
    // The hook owns its own native message pump. No UI work, clipboard IO,
    // synchronous pipe writes or document reads may execute on this thread.
    internal sealed class KeyboardMonitor : IDisposable {
        const uint Quit=0x12, PingMessage=0x8001,InitialFocusMessage=0x8002;
        readonly KeyboardInput[] input;
        int readPosition,writePosition,generation;
        readonly int capacity;
        readonly Func<KeyboardInput,bool> activity;
        readonly ManualResetEvent started=new ManualResetEvent(false),ping=new ManualResetEvent(false);
        readonly Native.KeyboardProc callback;
        readonly Native.WinEventProc focusCallback;
        readonly bool[] down=new bool[256];
        Thread thread;
        IntPtr hook,focusHook,foregroundHook;
        volatile bool disposed,enabled;
        volatile string error;
        volatile uint threadId;
        int dropped;
        long lastObserved;
        int initialFocusRequested;
        bool caps,num,scroll;
        public bool Enabled {
            get{return enabled;}
            set {
                bool previous=enabled;enabled=value;
                if(value && !previous) {
                    Interlocked.Exchange(ref initialFocusRequested,1);
                    if(threadId!=0)PostThreadMessage(threadId,InitialFocusMessage,UIntPtr.Zero,IntPtr.Zero);
                }
            }
        }
        public string Error {get{return error;}}
        public bool Running {get{return thread!=null && thread.IsAlive && hook!=IntPtr.Zero;}}
        public uint ThreadId {get{return threadId;}}
        public int Dropped {get{return Interlocked.CompareExchange(ref dropped,0,0);}}
        public long LastObserved {get{return Interlocked.Read(ref lastObserved);}}
        public KeyboardMonitor(Func<KeyboardInput,bool> pulse,bool startNative=true,int queueCapacity=8192) {
            activity=pulse;capacity=Math.Max(1,queueCapacity);input=new KeyboardInput[capacity];callback=OnHook;focusCallback=OnFocus;
            if(startNative) {
                thread=new Thread(Run){IsBackground=true,Name="TypeTrail keyboard input"};
                thread.SetApartmentState(ApartmentState.STA);thread.Start();
                if(!started.WaitOne(2500))error="Keyboard hook startup timed out";
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct Message {
            public IntPtr Window;public uint Id;public UIntPtr WParam;public IntPtr LParam;
            public uint Time;public int X,Y;public uint Private;
        }
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll",EntryPoint="PeekMessageW")] static extern bool PeekMessage(out Message value,IntPtr window,uint min,uint max,uint remove);
        [DllImport("user32.dll",EntryPoint="GetMessageW")] static extern int GetMessage(out Message value,IntPtr window,uint min,uint max);
        [DllImport("user32.dll",EntryPoint="DispatchMessageW")] static extern IntPtr DispatchMessage(ref Message value);
        [DllImport("user32.dll",EntryPoint="PostThreadMessageW",SetLastError=true)] static extern bool PostThreadMessage(uint thread,uint message,UIntPtr w,IntPtr l);
        void Run() {
            try {
                threadId=GetCurrentThreadId();Message value;
                PeekMessage(out value,IntPtr.Zero,0,0,0);
                for(int i=0;i<down.Length;i++)down[i]=(Native.GetAsyncKeyState(i)&0x8000)!=0;
                caps=(Native.GetKeyState(0x14)&1)!=0;num=(Native.GetKeyState(0x90)&1)!=0;scroll=(Native.GetKeyState(0x91)&1)!=0;
                hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);
                if(hook==IntPtr.Zero){error="Keyboard hook could not be installed";return;}
                focusHook=Native.SetWinEventHook(0x8005,0x800E,IntPtr.Zero,focusCallback,0,0,2);
                foregroundHook=Native.SetWinEventHook(3,3,IntPtr.Zero,focusCallback,0,0,2);
                if(focusHook==IntPtr.Zero || foregroundHook==IntPtr.Zero)error="Focus event hooks could not be installed";
                started.Set();
                while(!disposed) {
                    int result=GetMessage(out value,IntPtr.Zero,0,0);
                    if(result<=0){if(result<0)error="Keyboard message pump failed";break;}
                    if(value.Id==PingMessage){ping.Set();continue;}
                    if(value.Id==InitialFocusMessage){EmitInitialFocus();continue;}
                    DispatchMessage(ref value);
                }
            }catch(Exception e){error="Keyboard monitor: "+e.GetType().Name;}
            finally {
                IntPtr previous=hook;hook=IntPtr.Zero;
                if(previous!=IntPtr.Zero)Native.UnhookWindowsHookEx(previous);
                if(focusHook!=IntPtr.Zero)Native.UnhookWinEvent(focusHook);
                if(foregroundHook!=IntPtr.Zero)Native.UnhookWinEvent(foregroundHook);
                focusHook=foregroundHook=IntPtr.Zero;
                started.Set();ping.Set();
            }
        }
        static KeyboardInput Target() {
            IntPtr window=Native.GetForegroundWindow();uint pid;
            uint tid=Native.GetWindowThreadProcessId(window,out pid);
            var gui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
            IntPtr focus=Native.GetGUIThreadInfo(tid,ref gui)?gui.hwndFocus:IntPtr.Zero;
            uint inputPid=pid,inputThread=tid;
            if(focus!=IntPtr.Zero && Native.GetAncestor(focus,2)==Native.GetAncestor(window,2))inputThread=Native.GetWindowThreadProcessId(focus,out inputPid);
            else focus=window;
            return new KeyboardInput {Window=window,Focus=focus,ProcessId=pid,InputProcessId=inputPid,ThreadId=inputThread,Layout=Native.GetKeyboardLayout(inputThread)};
        }
        void EmitInitialFocus() {
            if(!enabled || disposed || Interlocked.Exchange(ref initialFocusRequested,0)==0)return;
            var value=Target();value.FocusEvent=true;value.FocusKind=3;value.Tick=Native.GetTickCount64();
            value.Timestamp=Stopwatch.GetTimestamp();Enqueue(value);
        }
        void OnFocus(IntPtr handle,uint kind,IntPtr window,int objectId,int childId,uint tid,uint time) {
            if(!enabled || disposed || (kind!=3 && kind!=0x8005 && kind!=0x800E))return;
            try {
                var value=Target();
                // WinEvent delivery can lag a rapid foreground switch. Never
                // relabel an old window's notification as the current editor.
                if(window!=IntPtr.Zero && Native.GetAncestor(window,2)!=Native.GetAncestor(value.Window,2))return;
                ulong now=Native.GetTickCount64(),original=(now&0xFFFFFFFF00000000UL)|time;
                if(original>now && original-now>0x7FFFFFFFUL)original-=0x100000000UL;
                if(now>original && now-original>0x7FFFFFFFUL)original+=0x100000000UL;
                value.FocusEvent=true;value.FocusKind=kind;value.ObjectId=objectId;value.ChildId=childId;
                value.Tick=original;value.Timestamp=Stopwatch.GetTimestamp();Enqueue(value);
            }catch(Exception e){error="Focus callback: "+e.GetType().Name;}
        }
        void UpdateState(int key,bool pressed) {
            if(key<0 || key>=256)return;
            bool prior=down[key];down[key]=pressed;
            if(pressed && !prior){if(key==0x14)caps=!caps;if(key==0x90)num=!num;if(key==0x91)scroll=!scroll;}
            if(key==0xA0 || key==0xA1)down[0x10]=down[0xA0]||down[0xA1];
            if(key==0xA2 || key==0xA3)down[0x11]=down[0xA2]||down[0xA3];
            if(key==0xA4 || key==0xA5)down[0x12]=down[0xA4]||down[0xA5];
        }
        IntPtr OnHook(int code,IntPtr message,IntPtr data) {
            try {
                int kind=message.ToInt32();
                if(code>=0 && (kind==0x100 || kind==0x104 || kind==0x101 || kind==0x105)) {
                    int key=Marshal.ReadInt32(data);bool pressed=kind==0x100 || kind==0x104;
                    UpdateState(key,pressed);
                    if(pressed && enabled && !disposed) {
                        EmitInitialFocus();
                        int captureGeneration=Thread.VolatileRead(ref generation);
                        var value=Target();value.Key=key;value.Scan=Marshal.ReadInt32(data,4);value.Flags=unchecked((uint)Marshal.ReadInt32(data,8));
                        value.Tick=Native.GetTickCount64();value.Timestamp=Stopwatch.GetTimestamp();
                        value.Ctrl=down[0x11];value.Alt=down[0x12];value.Shift=down[0x10];value.Win=down[0x5B]||down[0x5C];
                        value.Caps=caps;value.Num=num;value.Scroll=scroll;
                        value.AltGr=value.Ctrl && value.Alt && down[0xA5];
                        Interlocked.Exchange(ref lastObserved,value.Timestamp);
                        if(activity!=null && KeyboardInputPolicy.Modifies(key,value.Ctrl,value.Alt,value.Win,value.Shift,value.AltGr))
                            try{value.ActivityPulsed=activity(value);}catch(Exception e){error="Keyboard activity: "+e.GetType().Name;}
                        value.Generation=captureGeneration;EnqueueOwned(value);
                    }
                }
            }catch(Exception e){error="Keyboard callback: "+e.GetType().Name;}
            return Native.CallNextHookEx(hook,code,message,data);
        }
        internal void Enqueue(KeyboardInput value) {
            if(value==null)return;
            value.Generation=Thread.VolatileRead(ref generation);EnqueueOwned(value);
        }
        void EnqueueOwned(KeyboardInput value) {
            // One producer (the hook thread), one consumer (the controller).
            // UI allocation or a preempted consumer cannot hold a callback lock.
            int write=Thread.VolatileRead(ref writePosition);
            if((uint)(write-Thread.VolatileRead(ref readPosition))>=(uint)capacity){Interlocked.Increment(ref dropped);return;}
            input[(uint)write%(uint)capacity]=value;
            Thread.VolatileWrite(ref writePosition,unchecked(write+1));
        }
        public List<KeyboardInput> Drain() {
            var values=new List<KeyboardInput>();
            int read=Thread.VolatileRead(ref readPosition),end=Thread.VolatileRead(ref writePosition),current=Thread.VolatileRead(ref generation);
            while(read!=end) {
                int slot=(int)((uint)read%(uint)capacity);var value=input[slot];input[slot]=null;
                read=unchecked(read+1);Thread.VolatileWrite(ref readPosition,read);
                if(value!=null && value.Generation==current)values.Add(value);
            }
            return values;
        }
        public void Clear() {Interlocked.Increment(ref generation);Drain();}
        public bool Ping(int timeout) {
            if(!Running)return false;
            ping.Reset();return PostThreadMessage(threadId,PingMessage,UIntPtr.Zero,IntPtr.Zero) && ping.WaitOne(Math.Max(0,timeout));
        }
        public void Dispose() {
            if(disposed)return;enabled=false;disposed=true;
            if(threadId!=0)PostThreadMessage(threadId,Quit,UIntPtr.Zero,IntPtr.Zero);
            if(thread!=null && Thread.CurrentThread!=thread)thread.Join(1500);
            Clear();
            // Keep wait handles alive if a still-exiting hook thread owns them.
            if(thread==null || !thread.IsAlive){started.Dispose();ping.Dispose();}
        }
    }
}
