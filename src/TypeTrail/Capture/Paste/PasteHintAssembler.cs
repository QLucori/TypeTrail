using System;
using System.Text;

namespace TypeTrail {
    // Clipboard content is evidence of a command, never a committed text event.
    internal sealed class PasteHintAssembler {
        readonly StringBuilder text = new StringBuilder();
        uint pid;
        uint nextIndex;
        ulong window, tick;
        bool active;
        public void Reset() { active=false; text.Clear(); }
        public bool Append(CommittedInput input, int limit, out string completed) {
            completed=null;
            if(input.Kind!=4 || (input.Flags&64)==0) return false;
            if((input.Flags&256)!=0) { Reset(); active=true; nextIndex=0; pid=input.ProcessId; window=input.Window; tick=input.Tick; }
            if(!active || pid!=input.ProcessId || window!=input.Window ||
                (input.Flags>>10)!=nextIndex ||
                (input.Tick>=tick ? input.Tick-tick : tick-input.Tick)>1000) { Reset(); return false; }
            string chunk=input.Text??"";
            if(text.Length+chunk.Length>Math.Max(0,limit)) { Reset(); return false; }
            text.Append(chunk);
            nextIndex++;
            if((input.Flags&128)==0) return false;
            completed=History.Normalize(text.ToString());
            Reset();
            return completed.Length>0;
        }
    }
}
