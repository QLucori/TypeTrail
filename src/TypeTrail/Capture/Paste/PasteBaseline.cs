using System;
using System.Globalization;

namespace TypeTrail {
    internal sealed class PasteBaseline {
        public uint ProcessId;
        public ulong Window, Tick;
        public int Length, Start, End;
        public bool Matches(CommittedInput input) {
            return ProcessId==input.ProcessId && Window==input.Window &&
                (input.Tick>=Tick?input.Tick-Tick:Tick-input.Tick)<=1000;
        }
        public static PasteBaseline Decode(CommittedInput input) {
            if(input.Kind!=5 || (input.Flags&64)==0) return null;
            string[] parts=(input.Text??"").Split(','); int length,start,end;
            if(parts.Length!=3 || !Int32.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out length) ||
                !Int32.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out start) ||
                !Int32.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out end) ||
                length>500000 || start>end || end>length) return null;
            return new PasteBaseline {ProcessId=input.ProcessId,Window=input.Window,Tick=input.Tick,Length=length,Start=start,End=end};
        }
    }
}
