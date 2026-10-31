using System;
using System.Text;

namespace TypeTrail {
    internal static class ScintillaCapture {
        // Scintilla's standard Windows text message is marshalled by User32.
        // Never pass local pointers to its private cross-process messages.
        internal static Sample Read(IntPtr focus, uint pid, string source, int maxChars) {
            IntPtr readOnly, document, length, copied;
            if (Native.SendValueTimeout(focus, 2140, IntPtr.Zero, IntPtr.Zero, 2, 150, out readOnly) == IntPtr.Zero) return null;
            if (readOnly != IntPtr.Zero) return new Sample { Status = "readonly", Source = source };
            if (Native.SendValueTimeout(focus, 2357, IntPtr.Zero, IntPtr.Zero, 2, 150, out document) == IntPtr.Zero || document == IntPtr.Zero) return null;
            if (Native.SendValueTimeout(focus, 0xE, IntPtr.Zero, IntPtr.Zero, 2, 150, out length) == IntPtr.Zero) return null;
            int capacity = (int)Math.Min(Math.Max(0, length.ToInt64()), 200000) + 1;
            var buffer = new StringBuilder(capacity);
            if (Native.SendMessageTimeout(focus, 0xD, (IntPtr)capacity, buffer, 2, 150, out copied) == IntPtr.Zero) return null;
            if (length.ToInt64() > 0 && copied.ToInt64() == 0) return null;
            string text = buffer.ToString();
            return new Sample { Status = "ok", Source = source, Key = pid + ":scintilla:" + focus.ToInt64() + ":" + document.ToInt64(), Text = History.Tail(text, maxChars), Truncated = text.Length > maxChars || length.ToInt64() >= capacity };
        }
    }
}
