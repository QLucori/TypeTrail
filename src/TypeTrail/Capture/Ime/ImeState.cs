using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace TypeTrail {
    // A small bridge to the modern UIA TextEdit pattern, absent from .NET 4.8's
    // managed UIA wrapper. ABI slots are verified against Microsoft's
    // win32metadata UIAutomationClient.h. All calls stay in the capture process.
    internal sealed class ImeState : IDisposable {
        object automationObject;
        IntPtr automation, automation3, element, pattern;
        string key;
        EditEvents events;
        readonly FinalizedInputQueue finalizedInputs = new FinalizedInputQueue();
        public string CompositionBase { get; private set; }
        static readonly Guid AutomationId = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
        static readonly Guid Automation3Id = new Guid("73d768da-9b51-4b89-936e-c209290973e7");
        static readonly Guid PatternId = new Guid("17e21576-996c-4870-99d9-bff323380c06");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetObject(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPattern(IntPtr self, int id, ref Guid iid, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetRuntimeId(IntPtr self, [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddEditEvent(IntPtr self, IntPtr target, int scope, int change, IntPtr cache, [MarshalAs(UnmanagedType.Interface)] IEditEvents handler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RemoveEditEvent(IntPtr self, IntPtr target, [MarshalAs(UnmanagedType.Interface)] IEditEvents handler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MoveEndpoint(IntPtr self, int endpoint, IntPtr other, int otherEndpoint);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReadText(IntPtr self, int count, [MarshalAs(UnmanagedType.BStr)] out string result);
        static T Method<T>(IntPtr instance, int slot) where T : class {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T)) as T;
        }
        void Initialize() {
            if (automation != IntPtr.Zero) return;
            automationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9")));
            IntPtr unknown = Marshal.GetIUnknownForObject(automationObject);
            try {
                Guid iid = AutomationId; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out automation));
                iid = Automation3Id; Marshal.QueryInterface(unknown, ref iid, out automation3);
            } finally { Marshal.Release(unknown); }
        }
        public int Read(string expectedRuntimeId, out bool finalized) {
            return Read(expectedRuntimeId, out finalized, null);
        }
        public int Read(string expectedRuntimeId, out bool finalized, FinalizedInput binding) {
            return Read(expectedRuntimeId,out finalized,binding,true);
        }
        public int Read(string expectedRuntimeId, out bool finalized, FinalizedInput binding, bool comparisonText,InputActivityStamp[] inputActivity=null,string activityField=null) {
            finalized = false;
            CompositionBase = null;
            try {
                Initialize();
                string subscriptionKey=(binding==null?"":binding.ProcessId+":"+binding.Window+":")+expectedRuntimeId;
                if (key != subscriptionKey) {
                    Unwatch();
                    IntPtr focused;
                    if (Method<GetObject>(automation, 8)(automation, out focused) < 0 || focused == IntPtr.Zero) return -1;
                    element = focused;
                    int[] ids;
                    if (Method<GetRuntimeId>(element, 4)(element, out ids) < 0 || String.Join(".", ids) != expectedRuntimeId) { Unwatch(); return -1; }
                    key = subscriptionKey;
                    Guid iid = PatternId;
                    if (Method<GetPattern>(element, 14)(element, 10032, ref iid, out pattern) < 0) pattern = IntPtr.Zero;
                    // A focused custom/root element may not implement TextEdit,
                    // while its actual editor descendant raises finalization events.
                    if (automation3 != IntPtr.Zero) {
                        if(binding!=null)binding.FieldIdentity=activityField??binding.ProcessId+":"+expectedRuntimeId;
                        events = new EditEvents(binding, finalizedInputs);
                        events.ObserveKeyboard(inputActivity);
                        Method<AddEditEvent>(automation3, 64)(automation3, element, 7, 2, IntPtr.Zero, events);
                        Method<AddEditEvent>(automation3, 64)(automation3, element, 7, 3, IntPtr.Zero, events);
                    }
                }
                if(events!=null)events.ObserveKeyboard(inputActivity);
                if (pattern == IntPtr.Zero) return -1;
                IntPtr range;
                int hr = Method<GetObject>(pattern, 9)(pattern, out range);
                if (hr < 0) return -1;
                bool active = range != IntPtr.Zero;
                if(!active && events!=null)events.CompositionEnded();
                if (range != IntPtr.Zero) {
                    try { if(comparisonText)CompositionBase = OutsideComposition(range); }
                    finally { Marshal.Release(range); }
                }
                if (events != null && events.State == 0) {
                    finalized = Interlocked.Exchange(ref events.FinalizedPending, 0) != 0;
                    // Chromium can briefly retain its composition range after finalization.
                    if (!active) events.State = -1;
                    return 0;
                }
                return active ? 1 : 0;
            } catch { Unwatch(); return -1; }
        }
        public string TakeCommittedText() {
            var text = new StringBuilder();
            foreach (var input in TakeCommittedInputs()) text.Append(input.Text);
            return text.ToString();
        }
        public List<FinalizedInput> TakeCommittedInputs() { return finalizedInputs.Take(); }
        public bool CommitOverflow { get { return finalizedInputs.Overflowed; } }
        string OutsideComposition(IntPtr composition) {
            IntPtr document = IntPtr.Zero, before = IntPtr.Zero, after = IntPtr.Zero;
            try {
                // IUIAutomationTextPattern::DocumentRange, then two cloned ranges
                // ending before / beginning after the active composition.
                if (Method<GetObject>(pattern, 7)(pattern, out document) < 0 || document == IntPtr.Zero) return null;
                if (Method<GetObject>(document, 3)(document, out before) < 0 || before == IntPtr.Zero) return null;
                if (Method<GetObject>(document, 3)(document, out after) < 0 || after == IntPtr.Zero) return null;
                if (Method<MoveEndpoint>(before, 15)(before, 1, composition, 0) < 0 || Method<MoveEndpoint>(after, 15)(after, 0, composition, 1) < 0) return null;
                string prefix, suffix;
                if (Method<ReadText>(before, 12)(before, 50001, out prefix) < 0 || Method<ReadText>(after, 12)(after, 50001, out suffix) < 0) return null;
                if ((prefix ?? "").Length > 50000 || (suffix ?? "").Length > 50000) return null;
                return (prefix ?? "") + (suffix ?? "");
            } catch { return null; }
            finally {
                if (after != IntPtr.Zero) Marshal.Release(after);
                if (before != IntPtr.Zero) Marshal.Release(before);
                if (document != IntPtr.Zero) Marshal.Release(document);
            }
        }
        void Unwatch() {
            if (events != null) {
                events.Enabled = false;
                if (automation3 != IntPtr.Zero && element != IntPtr.Zero) {
                    try { Method<RemoveEditEvent>(automation3, 65)(automation3, element, events); } catch {}
                }
                events = null;
            }
            if (pattern != IntPtr.Zero) { Marshal.Release(pattern); pattern = IntPtr.Zero; }
            if (element != IntPtr.Zero) { Marshal.Release(element); element = IntPtr.Zero; }
            key = null;
        }
        public void Dispose() {
            Unwatch();
            if (automation3 != IntPtr.Zero) { Marshal.Release(automation3); automation3 = IntPtr.Zero; }
            if (automation != IntPtr.Zero) { Marshal.Release(automation); automation = IntPtr.Zero; }
            if (automationObject != null) { Marshal.FinalReleaseComObject(automationObject); automationObject = null; }
        }
    }
    [ComVisible(true), Guid("92faa680-e704-4156-931a-e32d5bb38f3f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEditEvents {
        [PreserveSig] int HandleTextEditTextChangedEvent(IntPtr sender, int changeType, IntPtr eventStrings);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EditEvents : IEditEvents {
        const int MaxCommits = 2048, MaxPendingCharacters = 500000;
        readonly FinalizedInputQueue committed;
        readonly FinalizedInput binding;
        readonly object authorityGate=new object();
        readonly List<InputActivityStamp> keyboard=new List<InputActivityStamp>();
        sealed class WaitingFinal {public FinalizedInput Input;public long OriginStamp,FinalStamp;}
        readonly List<WaitingFinal> waiting=new List<WaitingFinal>();
        int waitingCharacters;
        ulong preeditTick;
        long preeditStamp;
        bool authorizedComposition;
        bool compositionEnded;
        ulong compositionEndedTick;
        internal void CompositionEnded() {
            lock(authorityGate)if(State==1 && !compositionEnded) {compositionEnded=true;compositionEndedTick=Native.GetTickCount64();}
        }
        bool HasKeyboard(long before,ulong tick) {
            foreach(var key in keyboard)if(key.Timestamp>0 && key.Timestamp<=before && key.Tick<=tick && tick-key.Tick<=5000)return true;
            return false;
        }
        internal void ObserveKeyboard(InputActivityStamp[] activity) {
            var released=new List<FinalizedInput>();
            lock(authorityGate) {
                keyboard.Clear();
                if(activity!=null)foreach(var key in activity)if(key!=null && key.Timestamp>0 && key.Tick>0 && keyboard.Count<512)keyboard.Add(new InputActivityStamp {Tick=key.Tick,Timestamp=key.Timestamp});
                if(preeditStamp!=0 && HasKeyboard(preeditStamp,preeditTick))authorizedComposition=true;
                for(int i=waiting.Count-1;i>=0;i--) {
                    var value=waiting[i];ulong at=value.Input.OriginTick==0?value.Input.Tick:value.Input.OriginTick;
                    if(HasKeyboard(value.OriginStamp==0?value.FinalStamp:value.OriginStamp,at)) {
                        released.Add(value.Input);waitingCharacters-=value.Input.Text.Length;waiting.RemoveAt(i);
                    }
                }
            }
            released.Reverse();foreach(var input in released)committed.Append(input);
        }
        public EditEvents() : this(null, new FinalizedInputQueue()) {}
        internal EditEvents(FinalizedInput source, FinalizedInputQueue queue) {
            binding = FinalizedInput.Capture(source, null); committed = queue;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetProperty(IntPtr self, int propertyId, [MarshalAs(UnmanagedType.Struct)] out object result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetRuntimeIdentity(IntPtr self,[MarshalAs(UnmanagedType.SafeArray,SafeArraySubType=VarEnum.VT_I4)]out int[] result);
        [DllImport("oleaut32.dll")] static extern uint SafeArrayGetDim(IntPtr array);
        [DllImport("oleaut32.dll")] static extern int SafeArrayGetVartype(IntPtr array, out ushort type);
        [DllImport("oleaut32.dll")] static extern int SafeArrayGetLBound(IntPtr array, uint dimension, out int bound);
        [DllImport("oleaut32.dll")] static extern int SafeArrayGetUBound(IntPtr array, uint dimension, out int bound);
        [DllImport("oleaut32.dll")] static extern int SafeArrayGetElement(IntPtr array, ref int index, out IntPtr value);
        [DllImport("oleaut32.dll")] static extern uint SysStringLen(IntPtr value);
        public volatile int State = -1;
        public int FinalizedPending;
        public volatile bool Enabled = true;
        public int HandleTextEditTextChangedEvent(IntPtr sender, int changeType, IntPtr eventStrings) {
            long receivedStamp=Stopwatch.GetTimestamp();ulong receivedTick=Native.GetTickCount64();
            if (Enabled && (changeType == 2 || changeType == 3) && !ProtectedSender(sender)) {
                if (changeType == 2) {
                    lock(authorityGate) {
                        if(State!=1 || compositionEnded) {preeditTick=receivedTick;preeditStamp=receivedStamp;authorizedComposition=false;compositionEnded=false;}
                        State=1;
                        authorizedComposition=authorizedComposition || HasKeyboard(preeditStamp,preeditTick);
                    }
                }
                if (changeType == 3) {
                    ulong origin;long originStamp,finalStamp=receivedStamp;bool authorized;
                    ulong finalTick=receivedTick;
                    lock(authorityGate) {
                        origin=preeditTick;originStamp=preeditStamp;
                        authorized=(authorizedComposition && (!compositionEnded || finalTick-compositionEndedTick<=1000)) || HasKeyboard(originStamp==0?finalStamp:originStamp,origin==0?finalTick:origin);
                        preeditTick=0;preeditStamp=0;authorizedComposition=false;compositionEnded=false;
                    }
                    State = 0; Interlocked.Exchange(ref FinalizedPending, 1);
                    // Admission happened above while enabled. Keep this event if
                    // the subscription retires while its SAFEARRAY is decoded.
                    bool overflow;
                    string text = DecodeFinalizedStrings(eventStrings, out overflow);
                    if (overflow) committed.ReportOverflow();
                    if (text.Length > 0) {
                        var input=FinalizedInput.Capture(binding,text);
                        input.Tick=finalTick;input.OriginTick=origin;input.Timestamp=finalStamp;input.OriginTimestamp=originStamp;
                        input.FieldIdentity=SenderIdentity(sender)??input.FieldIdentity;
                        if(authorized)committed.Append(input);
                        else lock(authorityGate) {
                            if(waiting.Count<32 && text.Length<=500000-waitingCharacters) {waiting.Add(new WaitingFinal {Input=input,OriginStamp=originStamp,FinalStamp=finalStamp});waitingCharacters+=text.Length;}
                            else committed.ReportOverflow();
                        }
                    }
                }
            }
            return 0;
        }
        static bool ProtectedSender(IntPtr sender) {
            if (sender == IntPtr.Zero) return true;
            try {
                IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(sender), 10 * IntPtr.Size);
                var get = (GetProperty)Marshal.GetDelegateForFunctionPointer(method, typeof(GetProperty));
                object password;
                int result = get(sender, 30019, out password);
                // Unknown providers are not allowed to bypass password exclusion.
                if(result<0 || !(password is bool) || (bool)password)return true;
                object name,readOnly,control;
                if(get(sender,30005,out name)>=0 && name is string && SensitiveFieldRules.Name((string)name))return true;
                return get(sender,30003,out control)>=0 && control is int && ((int)control==50004 || (int)control==50003) && get(sender,30046,out readOnly)>=0 && readOnly is bool && (bool)readOnly;
            } catch { return true; }
        }
        static string SenderIdentity(IntPtr sender) {
            try {
                var runtime=(GetRuntimeIdentity)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(sender),4*IntPtr.Size),typeof(GetRuntimeIdentity));
                int[] ids; if(runtime(sender,out ids)<0 || ids==null)return null;
                var get=(GetProperty)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(sender),10*IntPtr.Size),typeof(GetProperty));
                object pid;if(get(sender,30002,out pid)<0 || !(pid is int))return null;
                return (int)pid+":"+String.Join(".",ids);
            }catch{return null;}
        }
        internal static string DecodeFinalizedStrings(IntPtr array) {
            bool overflow;
            return DecodeFinalizedStrings(array, out overflow);
        }
        static string DecodeFinalizedStrings(IntPtr array, out bool overflow) {
            overflow = false;
            if (array == IntPtr.Zero) return "";
            try {
                ushort type; int lower, upper;
                if (SafeArrayGetDim(array) != 1 || SafeArrayGetVartype(array, out type) < 0 || type != 8 || SafeArrayGetLBound(array, 1, out lower) < 0 || SafeArrayGetUBound(array, 1, out upper) < 0) return "";
                long count = (long)upper - lower + 1;
                if (count < 1) return "";
                if (count > MaxCommits) { overflow = true; return ""; }
                var value = new StringBuilder();
                for (long step = 0; step < count; step++) {
                    int index = checked((int)((long)lower + step)); IntPtr item;
                    if (SafeArrayGetElement(array, ref index, out item) < 0) return "";
                    try {
                        // Inspect the marshaled BSTR's declared size before
                        // allocating a managed copy of an unbounded provider value.
                        if(item!=IntPtr.Zero && SysStringLen(item)>(uint)(MaxPendingCharacters-value.Length)) {overflow=true;return "";}
                        string text = item == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(item);
                        value.Append(text);
                    } finally { if (item != IntPtr.Zero) Marshal.FreeBSTR(item); }
                }
                return value.ToString();
            } catch { return ""; }
        }
        internal void AcceptFinalizedStrings(IntPtr array, bool protectedSender) {
            if (protectedSender || !Enabled) return;
            bool overflow;
            string text = DecodeFinalizedStrings(array, out overflow);
            if (overflow) committed.ReportOverflow();
            if (text.Length > 0) committed.Append(FinalizedInput.Capture(binding, text));
        }
        public string TakeCommittedText() {
            var text = new StringBuilder();
            foreach (var item in committed.Take()) text.Append(item.Text);
            return text.ToString();
        }
        public void ClearCommittedText() { committed.Clear(); }
    }
}
