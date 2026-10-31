using System;
using System.Text;
using System.Collections.Generic;

namespace TypeTrail {
    // A short, volatile hint for the first read of an editor. It is never saved
    // on its own: the reader must find these characters at the editor's caret.
    internal sealed class TypingEvidence {
        readonly StringBuilder text = new StringBuilder();
        string deadKey;
        ulong appendTick,deadTick;
        sealed class HintSpan {public int Length;public ulong Tick;}
        readonly List<HintSpan> spans=new List<HintSpan>();
        public string Text { get { return text.ToString(); } }
        public void Clear() { text.Clear(); deadKey=null;spans.Clear(); }
        public void Consume(int count) {
            if(count<=0 || count>text.Length)return;
            text.Remove(0,count);
            while(count>0 && spans.Count>0) {int removed=Math.Min(count,spans[0].Length);spans[0].Length-=removed;count-=removed;if(spans[0].Length==0)spans.RemoveAt(0);}
        }
        public void ConsumeBefore(ulong tick) {
            int count=0;foreach(var span in spans) {if(span.Tick>=tick)break;count+=span.Length;}
            Consume(count);if(deadKey!=null && deadTick<tick)deadKey=null;
        }
        internal void AppendAt(string value,ulong tick) {ulong previous=appendTick;appendTick=tick;Append(value);appendTick=previous;}
        public void Append(string value) {
            if (String.IsNullOrEmpty(value)) return;
            text.Append(value);
            if(spans.Count>0 && spans[spans.Count-1].Tick==appendTick)spans[spans.Count-1].Length+=value.Length;
            else spans.Add(new HintSpan {Length=value.Length,Tick=appendTick});
            if (text.Length > 2048) {
                int start = text.Length - 2048;
                if (Char.IsLowSurrogate(text[start]) && Char.IsHighSurrogate(text[start - 1])) start++;
                Consume(start);
            }
        }
        public void Backspace() {
            if(deadKey!=null) {deadKey=null;return;}
            if (text.Length == 0) return;
            int start=GraphemeText.LastStart(text.ToString());
            int remove=text.Length-start;text.Remove(start,remove);
            while(remove>0 && spans.Count>0) {var last=spans[spans.Count-1];int count=Math.Min(remove,last.Length);last.Length-=count;remove-=count;if(last.Length==0)spans.RemoveAt(spans.Count-1);}
        }
        internal void Translated(string value,bool dead) {
            if(String.IsNullOrEmpty(value))return;
            if(dead) {
                if(deadKey!=null) {Append(deadKey);deadKey=null;} else {deadKey=value;deadTick=appendTick;}
                return;
            }
            if(deadKey!=null) {
                string accent=deadKey;deadKey=null;
                if(value==" ") {Append(accent);return;}
                string mark=CombiningAccent(accent);
                if(mark!=null) {
                    try {
                        if(value.Normalize(NormalizationForm.FormD).EndsWith(mark,StringComparison.Ordinal)) {Append(value);return;}
                        int first=Char.IsHighSurrogate(value[0]) && value.Length>1 && Char.IsLowSurrogate(value[1])?2:1;
                        string composed=(value.Substring(0,first)+mark).Normalize(NormalizationForm.FormC);
                        if(composed.Length<=first) {Append(composed+value.Substring(first));return;}
                    } catch(ArgumentException) {}
                }
                Append(accent);
            }
            Append(value);
        }
        static string CombiningAccent(string value) {
            switch(value) {
                case "'":case "´":return "\u0301";
                case "`":return "\u0300";
                case "^":return "\u0302";
                case "~":return "\u0303";
                case "\"":case "¨":return "\u0308";
                case "°":return "\u030A";
                case "ˇ":return "\u030C";
                case "˘":return "\u0306";
                case "¯":return "\u0304";
                case "˙":return "\u0307";
                case "˛":return "\u0328";
                case "¸":return "\u0327";
                case "˝":return "\u030B";
                default:return null;
            }
        }
        public void Key(int key, int scan, bool ctrl, bool alt, bool win, IntPtr layout, bool altGr = false, byte[] snapshotState = null, ulong inputTick = 0) {
            appendTick=inputTick;
            if (ctrl && key == 86 && !alt && !win) return;
            if (((ctrl || alt) && !altGr) || win) { Clear(); return; }
            if (key == 8) { Backspace(); return; }
            if(key==9) {Clear();Append("\t");return;}
            if (key == 27 || key == 46 || (key >= 33 && key <= 40)) { Clear(); return; }
            if (key == 13) { Append("\n"); return; }
            if (key == 0xE7) { Translated(((char)scan).ToString(),false); return; }
            if (key < 32 || (key >= 112 && key <= 165)) return;
            if(key>255)return;
            var state = snapshotState!=null && snapshotState.Length==256?(byte[])snapshotState.Clone():new byte[256];
            state[key] = 0x80;
            if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; state[0xA5] = 0x80; }
            if(snapshotState==null) {
                if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) state[0x10] = 0x80;
                state[0x14] = (byte)(Native.GetKeyState(0x14) & 1);
            }
            var value = new StringBuilder(16);
            // Bit 2 leaves Windows' dead-key state untouched.
            int count = Native.ToUnicodeEx((uint)key, (uint)scan, state, value, value.Capacity, 4, layout);
            if (count > 0) Translated(value.ToString(0, Math.Min(count, value.Length)),false);
            else if (count < 0) Translated(value.ToString(),true);
        }
    }
    internal static class CaptureFlow {
        public static void IncludeInFlightInput(Sample sample, bool active, long serial, bool canInsert) {
            // A text read can start before a key/paste and finish after the text
            // changes. The activity captured when the request was sent is stale.
            if (active && serial > sample.InputSerial) {
                sample.Active = true; sample.InputSerial = serial; sample.CanInsert |= canInsert;
            }
        }
        public static bool Accept(long requestedFocus, long currentFocus, long requestedInput, long currentInput) {
            // Requests are serialized, so a later key does not make an earlier
            // response obsolete. Reject only a response for another focus epoch.
            return requestedFocus == currentFocus;
        }
    }
}
