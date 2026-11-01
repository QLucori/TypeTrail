using System;
using System.Collections.Generic;

namespace TypeTrail {
    // A phonetic key hint is not the committed word. This short-lived permit
    // joins an actual IME selection key with a safe, noncomposing TSF update.
    internal sealed class ImeConversionEvidence {
        sealed class Permit {
            public uint Pid,Serial,Thread;public ulong Root,Focus,Tick;public long Epoch;
            public int Budget,Used,LastChunk;public bool Started,Space,SpaceUsed,CanConvert;
            public string Literal;public bool LiteralAfterReturn;
        }
        readonly List<Permit> permits=new List<Permit>();
        public void Clear() {permits.Clear();}
        public void Select(uint pid,ulong root,ulong focus,long epoch,ulong tick,IntPtr layout,int key,string hint) {
            int language=(int)(layout.ToInt64()&0x3ff);
            bool asian=language==4 || language==17 || language==18;
            if(!asian && key!=32)return;
            if(key!=32 && key!=13 && (key<48 || key>57))return;
            int letters=0;foreach(char ch in hint??"")if((ch>='a'&&ch<='z')||(ch>='A'&&ch<='Z'))letters++;
            if((letters==0 && key!=32) || tick==0)return;
            permits.RemoveAll(p=>tick>=p.Tick && tick-p.Tick>5000);
            if(permits.Count>=32)permits.RemoveAt(0);
            permits.Add(new Permit {Pid=pid,Root=root,Focus=focus,Epoch=epoch,Tick=tick,Budget=Math.Min(4096,letters*2+2),Space=key==32,CanConvert=asian && letters>0,Literal=asian && key==13?LiteralWord(hint):null,LiteralAfterReturn=hint!=null && hint.IndexOf('\n')>=0 && hint.IndexOf('\n')<hint.Length-1});
        }
        static string LiteralWord(string hint) {
            if(String.IsNullOrEmpty(hint) || !hint.EndsWith("\n",StringComparison.Ordinal))return null;
            int end=hint.Length-1,start=end;
            while(start>0 && hint[start-1]>32 && hint[start-1]<127)start--;
            return start==end?null:hint.Substring(start,end-start);
        }
        public bool AcceptLiteral(CommittedInput input,uint rootPid,ulong root,long epoch,bool actualActivity,bool requirePriorReturn=false) {
            if(!actualActivity || input.Kind!=1 || (input.Flags&(4|8|1024))!=(4|1024) || input.InputSerial==0 || String.IsNullOrEmpty(input.Text))return false;
            // Return can commit a phonetic composition as literal English. Its
            // key hint is not an inserted newline. A later plain TSF range may
            // also include the preceding word or a provider paragraph marker.
            // Admit only the exact word selected by that same field's Return.
            string candidate=input.Text.TrimEnd('\r','\n','\t');
            for(int i=permits.Count-1;i>=0;i--) {
                Permit p=permits[i];
                if(!Same(p,input,rootPid,root,epoch) || p.Literal==null || p.Started || (requirePriorReturn && !p.LiteralAfterReturn))continue;
                int start=candidate.Length-p.Literal.Length;
                if(start<0 || !candidate.EndsWith(p.Literal,StringComparison.Ordinal) || (start>0 && !Char.IsWhiteSpace(candidate[start-1])))continue;
                input.Text=p.Literal;p.Literal=null;p.Started=true;p.CanConvert=false;p.SpaceUsed=true;
                return true;
            }
            return false;
        }
        static bool Converted(string value) {
            bool found=false;
            for(int i=0;i<(value??"").Length;i++) {
                int point=value[i];
                if(Char.IsHighSurrogate(value[i])) {
                    if(i+1>=value.Length || !Char.IsLowSurrogate(value[i+1]))return false;
                    point=Char.ConvertToUtf32(value[i],value[++i]);
                } else if(Char.IsLowSurrogate(value[i]))return false;
                // Unicode 16 CJK extension/compatibility blocks, rather than
                // treating every supplementary/non-ASCII character as an IME word.
                if((point>=0x3040&&point<=0x30ff)||(point>=0x3400&&point<=0x4dbf)||
                    (point>=0x4e00&&point<=0x9fff)||(point>=0xac00&&point<=0xd7af)||
                    (point>=0xf900&&point<=0xfaff)||(point>=0x20000&&point<=0x2ee5f)||
                    (point>=0x2f800&&point<=0x2fa1f)||(point>=0x30000&&point<=0x323af))found=true;
            }
            return found;
        }
        static bool Same(Permit p,CommittedInput input,uint pid,ulong root,long epoch) {
            return p.Pid==pid && p.Root==root && p.Focus==input.Window && p.Epoch==epoch && input.Tick>=p.Tick && input.Tick-p.Tick<=1500;
        }
        public bool AcceptSeparator(CommittedInput input,uint rootPid,ulong root,long epoch) {
            if(input.Kind!=1 || (input.Flags&(4|8|1024))!=(4|1024) || input.InputSerial==0 || input.Text!=" ")return false;
            // This is proof of the actual Space key, independent of a volatile
            // phonetic hint that may already contain the following word.
            foreach(var p in permits)if(Same(p,input,rootPid,root,epoch) && p.Space && !p.SpaceUsed) {
                p.SpaceUsed=true;p.CanConvert=false;return true;
            }
            return false;
        }
        public void ObserveAccepted(CommittedInput input,uint rootPid,ulong root,long epoch) {
            if(input.Kind!=1)return;
            for(int i=permits.Count-1;i>=0;i--) {
                Permit p=permits[i];
                if(p.Literal!=null && Same(p,input,rootPid,root,epoch) && (input.Text??"").TrimEnd('\r','\n','\t')==p.Literal) {
                    p.Literal=null;p.CanConvert=false;p.SpaceUsed=true;break;
                }
            }
            if(input.Text==" ") {
                foreach(var p in permits)if(Same(p,input,rootPid,root,epoch) && p.Space && !p.SpaceUsed) {p.SpaceUsed=true;p.CanConvert=false;return;}
            } else if((input.Flags&(2|8))!=0 && Converted(input.Text)) {
                for(int i=permits.Count-1;i>=0;i--)if(Same(permits[i],input,rootPid,root,epoch) && permits[i].CanConvert) {
                    permits[i].SpaceUsed=true;permits[i].CanConvert=false;return;
                }
            }
        }
        public bool Accept(CommittedInput input,uint rootPid,ulong root,long epoch,bool actualActivity) {
            if(input.Kind!=1 || (input.Flags&(4|8|1024))!=(4|1024) || input.InputSerial==0 || String.IsNullOrEmpty(input.Text))return false;
            int protocolLength=input.Text.Length;
            // A changed range may carry a provider's paragraph/tab marker in
            // the same packet as real text. Preserve the word, while Return/Tab
            // remain the responsibility of their actual literal key evidence.
            string candidate=input.Text.Replace("\r","").Replace("\n","").Replace("\t","");
            if(candidate.Length==0)return false;
            for(int i=permits.Count-1;i>=0;i--) {
                Permit p=permits[i];
                if(!Same(p,input,rootPid,root,epoch))continue;
                if(p.Started) {
                    // A provider's paragraph terminator is not an entered Return.
                    // Actual line breaks still use ordinary literal key evidence.
                    // Fixed protocol chunks and a trailing ASCII space may belong
                    // to the selected word; other control characters may not.
                    bool chunk=p.LastChunk==256 || (p.LastChunk==255 && Char.IsSurrogatePair(candidate,0));
                    if(p.Serial!=input.InputSerial || p.Thread!=input.ThreadId ||
                        (candidate!=" " && (!chunk || !Converted(candidate))))continue;
                } else if(!p.CanConvert || !actualActivity || !Converted(candidate))continue;
                if(candidate.Length>p.Budget-p.Used)return false;
                p.Started=true;p.SpaceUsed=true;p.Serial=input.InputSerial;p.Thread=input.ThreadId;p.Used+=candidate.Length;p.LastChunk=protocolLength;
                input.Text=candidate;return true;
            }
            return false;
        }
    }
}
