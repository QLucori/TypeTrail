using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace TypeTrail {
    internal static class ProtectionMetadata {
        internal static bool DenyReadOnly(ControlType type,bool valueReadOnly,bool textReadOnly) {
            return (type==ControlType.Edit || type==ControlType.ComboBox) && (valueReadOnly || textReadOnly);
        }
        public static void Attach(Sample sample,CaptureRequest request) {
            sample.ProtectionEpoch=request.ProtectionEpoch;
            IntPtr root=Native.GetForegroundWindow();uint pid;
            uint thread=Native.GetWindowThreadProcessId(root,out pid);
            sample.ProtectionWindow=unchecked((ulong)root.ToInt64());sample.ProtectionPid=pid;
            if(root==IntPtr.Zero || pid==request.ParentPid || (request.TargetWindow!=0 && request.TargetWindow!=root.ToInt64()))return;
            var gui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
            IntPtr focused=Native.GetGUIThreadInfo(thread,ref gui)?gui.hwndFocus:IntPtr.Zero;
            try {
                var element=AutomationElement.FocusedElement;if(element==null || !CaptureWorker.BelongsToWindow(element,root))return;
                var info=element.Current;
                uint focusPid;Native.GetWindowThreadProcessId(focused,out focusPid);
                if(info.ProcessId!=pid && (focused==IntPtr.Zero || info.ProcessId!=focusPid))return;
                string identity=info.ProcessId+":"+String.Join(".",element.GetRuntimeId());
                bool allowed=!info.IsPassword && !SensitiveFieldRules.Name(info.Name) && info.IsEnabled;
                var parent=element;
                for(int i=0;i<3 && allowed;i++) {
                    parent=TreeWalker.ControlViewWalker.GetParent(parent);
                    if(parent==null || parent.Current.ControlType==ControlType.Window)break;
                    if(parent.Current.IsPassword)allowed=false;
                }
                object pattern;
                bool editingField=info.ControlType==ControlType.Edit || info.ControlType==ControlType.ComboBox;
                if(allowed && editingField && element.TryGetCurrentPattern(ValuePattern.Pattern,out pattern) && DenyReadOnly(info.ControlType,((ValuePattern)pattern).Current.IsReadOnly,false))allowed=false;
                if(allowed && editingField && element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) {
                    object readOnly=((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                    if(readOnly is bool && DenyReadOnly(info.ControlType,false,(bool)readOnly))allowed=false;
                }
                // Verify the virtual field again. A stable top-level HWND alone
                // cannot prove that a password/ordinary DOM focus stayed put.
                var latest=AutomationElement.FocusedElement;
                var currentGui=new Native.GUITHREADINFO {cbSize=Marshal.SizeOf(typeof(Native.GUITHREADINFO))};
                if(latest==null || root!=Native.GetForegroundWindow() || !Native.GetGUIThreadInfo(thread,ref currentGui) || currentGui.hwndFocus!=focused ||
                    latest.Current.ProcessId+":"+String.Join(".",latest.GetRuntimeId())!=identity)return;
                sample.ProtectionKnown=true;sample.ProtectionAllowed=allowed;sample.ProtectionField=identity;sample.ProtectionTick=Native.GetTickCount64();
                return;
            } catch {}
            // Standard native editors have an explicit password/read-only style.
            // This fallback reads class/style metadata only, never a text value.
            if(focused==IntPtr.Zero || Native.GetAncestor(focused,2)!=Native.GetAncestor(root,2))return;
            var name=new StringBuilder(256);Native.GetClassName(focused,name,name.Capacity);bool rich;
            if(!NativeEditCapture.IsEditClass(name.ToString(),out rich))return;
            sample.ProtectionKnown=true;sample.ProtectionAllowed=(Native.GetWindowLong(focused,-16)&(0x20|0x800))==0;
            sample.ProtectionField="native:"+focused.ToInt64();sample.ProtectionTick=Native.GetTickCount64();
        }
    }
}
