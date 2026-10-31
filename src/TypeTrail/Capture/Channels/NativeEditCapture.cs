using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeTrail {
    internal static class NativeEditCapture {
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW",SetLastError=true)]
        static extern IntPtr GetSelection(IntPtr window,uint message,out int start,out int end,uint flags,uint timeout,out IntPtr result);
        internal static bool IsEditClass(string name, out bool rich) {
            name=(name??"").ToLowerInvariant();
            rich=name.StartsWith("richedit") || (name.StartsWith("windowsforms10.") && name.IndexOf(".richedit",StringComparison.Ordinal)>=0);
            return rich || name=="edit" || name.StartsWith("windowsforms10.edit.");
        }
        public static Sample Read(IntPtr window,uint pid,string source,int maxChars) {
            if(window==IntPtr.Zero) return null;
            uint owner; Native.GetWindowThreadProcessId(window,out owner);
            if(owner!=pid) return null;
            var name=new StringBuilder(256); Native.GetClassName(window,name,name.Capacity);
            bool rich; if(!IsEditClass(name.ToString(),out rich)) return null;
            int style=Native.GetWindowLong(window,-16);
            if((style&0x20)!=0) return new Sample {Status="protected",Source=source};
            if((style&0x800)!=0) return new Sample {Status="readonly",Source=source};
            maxChars=Math.Max(1,Math.Min(500000,maxChars));
            IntPtr result;
            if(Native.SendValueTimeout(window,0xE,IntPtr.Zero,IntPtr.Zero,2,150,out result)==IntPtr.Zero || result.ToInt64()<0 || result.ToInt64()>500000) return null;
            int expectedLength=(int)result.ToInt64();
            int capacity=Math.Max(maxChars,expectedLength);
            var buffer=new StringBuilder(capacity+1);
            if(Native.SendMessageTimeout(window,0xD,(IntPtr)buffer.Capacity,buffer,2,150,out result)==IntPtr.Zero) return null;
            string text=buffer.ToString();
            if(text.Length==capacity && expectedLength!=capacity)return null;
            int start,end,caret=-1;
            // EM_GETSEL is a system edit message: Windows marshals its DWORD
            // output pointers across processes. No remote memory access is used.
            if(GetSelection(window,0xB0,out start,out end,2,150,out result)!=IntPtr.Zero && start==end && start>=0) {
                if(rich) { if(start<=History.Normalize(text).Length) caret=start; }
                else if(start<=text.Length) caret=History.Normalize(text.Substring(0,start)).Length;
            }
            string normalized=History.Normalize(text),tail=History.Tail(normalized,maxChars);
            bool truncated=tail.Length<normalized.Length;
            if(caret>=0) {caret-=normalized.Length-tail.Length;if(caret<0 || caret>tail.Length)caret=-1;}
            return new Sample {Status="ok",Source=source,Key=pid+":hwnd:"+window.ToInt64(),Text=tail,Caret=caret,Truncated=truncated,FieldLength=normalized.Length,TextOffset=normalized.Length-tail.Length};
        }
    }
}
