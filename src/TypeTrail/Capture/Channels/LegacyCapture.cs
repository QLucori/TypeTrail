using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Accessibility;

namespace TypeTrail {
    internal static class LegacyCapture {
        public static Sample Read(IntPtr focus, uint pid, string source, int maxChars) {
            if (focus == IntPtr.Zero) return null;
            var handles = new List<object>();
            try {
                Guid iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
                object value;
                if (Native.AccessibleObjectFromWindow(focus, 0xFFFFFFFC, ref iid, out value) < 0 || value == null) return null;
                handles.Add(value);
                IAccessible accessible = value as IAccessible;
                if (accessible == null) return null;
                object child = 0;
                for (int i = 0; i < 8; i++) {
                    int state = Convert.ToInt32(accessible.get_accState(child));
                    if ((state & 0x20000000) != 0 || CaptureWorker.SensitiveName(accessible.get_accName(child))) return new Sample { Status = "protected", Source = source };
                    object focused = accessible.accFocus;
                    var nested = focused as IAccessible;
                    if (nested != null && !Object.ReferenceEquals(nested, accessible)) { accessible = nested; handles.Add(nested); child = 0; continue; }
                    if (focused is int) child = focused;
                    break;
                }
                int finalState = Convert.ToInt32(accessible.get_accState(child));
                if ((finalState & 0x20000000) != 0 || CaptureWorker.SensitiveName(accessible.get_accName(child))) return new Sample { Status = "protected", Source = source };
                if ((finalState & 0x41) != 0 || (finalState & 4) == 0 || Convert.ToInt32(accessible.get_accRole(child)) != 42) return null;
                string text = accessible.get_accValue(child);
                if (text == null) return null;
                return new Sample { Status = "ok", Source = source, Key = pid + ":msaa:" + focus.ToInt64() + ":" + child, Text = History.Tail(text, maxChars), Truncated = text.Length > maxChars };
            } catch { return null; }
            finally { for (int i = handles.Count - 1; i >= 0; i--) if (Marshal.IsComObject(handles[i])) try { Marshal.ReleaseComObject(handles[i]); } catch {} }
        }
    }
}
