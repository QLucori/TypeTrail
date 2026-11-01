using System;
using System.Collections.Generic;
using System.Linq;

namespace TypeTrail {
    // Weak character messages wait for metadata from their own focus epoch.
    // A later ordinary field may never authorize an earlier protected field.
    internal sealed class FieldAdmission {
        internal sealed class Item {
            public CommittedInput Input;
            public string Source,Field;
            public long Epoch;
        }
        sealed class Scope {
            public long Id;
            public uint Pid;
            public ulong Root,Focus,Start,End,Checked,DeferredUntil;
            public bool Known,Allowed;
            public string Field;
        }
        sealed class Pending {public Item Item;public Scope Scope;public bool NativeSafe;public string StableField;}
        readonly List<Scope> scopes=new List<Scope>();
        readonly List<Pending> pending=new List<Pending>();
        long serial;
        int characters;
        bool lost;
        public Action<string> Trace;
        void Note(string reason,Scope scope,CommittedInput input=null) {
            if(Trace==null)return;
            Trace("admission "+reason+" epoch="+(scope==null?0:scope.Id)+" rootPid="+(scope==null?0:scope.Pid)+" root="+(scope==null?0:scope.Root)+" focus="+(scope==null?0:scope.Focus)+" known="+(scope!=null && scope.Known)+" allow="+(scope!=null && scope.Allowed)+" checked="+(scope==null?0:scope.Checked)+(input==null?"":" pid="+input.ProcessId+" window="+input.Window+" tick="+input.Tick+" flags="+input.Flags+" serial="+input.InputSerial+" length="+(input.Text??"").Length));
        }
        public bool HasPending {get{return pending.Count>0;}}
        public void DeferPending(long epoch,ulong now) {
            Scope scope=scopes.FirstOrDefault(s=>s.Id==epoch);
            if(scope==null || scope.End!=0 || (scope.Known && !scope.Allowed) || scope.DeferredUntil!=0)return;
            Pending first=pending.FirstOrDefault(p=>p.Scope==scope && !p.NativeSafe && p.Item.Input.Kind!=3 && !Confirmed(p));
            if(first==null)return;
            // A stalled UIA provider can delay every client's metadata. Keep
            // only this uninterrupted field's actual input, bounded in memory.
            // Repeated retries do not renew the deadline or grant permission.
            ulong tick=first.Item.Input.Tick;
            if(tick>now || now-tick>60000)return;
            scope.DeferredUntil=tick>UInt64.MaxValue-60000?UInt64.MaxValue:tick+60000;
            Note("defer until="+scope.DeferredUntil,scope);
        }
        public bool IsDeferred(long epoch,ulong now) {
            return pending.Any(p=>p.Scope.Id==epoch && p.Scope.End==0 && p.Scope.DeferredUntil!=0 && now<=p.Scope.DeferredUntil &&
                now>=p.Item.Input.Tick && now-p.Item.Input.Tick>=1000 && !(p.Scope.Known && !p.Scope.Allowed) && !p.NativeSafe && !Confirmed(p));
        }
        public bool Lost {get{return lost;}}
        public bool TakeLoss() {bool result=lost;lost=false;return result;}
        public bool IsDenied(long epoch) {var scope=scopes.FirstOrDefault(s=>s.Id==epoch);return scope!=null && scope.Known && !scope.Allowed;}
        public string FieldFor(long epoch) {var scope=scopes.FirstOrDefault(s=>s.Id==epoch);return scope==null?null:scope.Field;}
        public void Reset() {scopes.Clear();pending.Clear();characters=0;lost=false;}
        public long Focus(uint pid,ulong root,ulong focus,ulong tick,bool notification) {
            Scope previous=scopes.LastOrDefault();
            if(previous!=null && !notification && previous.Pid==pid && previous.Root==root && previous.Focus==focus)return previous.Id;
            // An out-of-order notification cannot retroactively validate data.
            if(previous!=null && tick<previous.Start) tick=previous.Start;
            if(previous!=null)previous.End=tick;
            var value=new Scope {Id=++serial,Pid=pid,Root=root,Focus=focus,Start=scopes.Count==0?0:tick};
            scopes.Add(value);
            Note("focus start="+value.Start,value);
            TrimScopes();
            return value.Id;
        }
        bool Confirmed(Pending value) {
            return value.Scope.Known && value.Scope.Allowed && value.Item.Input.Tick<=value.Scope.Checked;
        }
        void TrimScopes() {
            while(scopes.Count>128) {
                Scope old=scopes[0];
                for(int i=pending.Count-1;i>=0;i--)if(pending[i].Scope==old) {
                    // Pending holds its original immutable scope reference. Its
                    // own positive proof survives pruning; an unknown/denied
                    // interval can never borrow a later field's permission.
                    Pending next=pending[i];
                    bool denied=old.Known && !old.Allowed;
                    if(!denied && (next.NativeSafe || Confirmed(next)))continue;
                    Note(denied?"drop-pruned-denied":"drop-pruned-unconfirmed",old,next.Item.Input);
                    characters-=(next.Item.Input.Text??"").Length;pending.RemoveAt(i);
                    if(!denied)lost=true;
                }
                scopes.RemoveAt(0);
            }
        }
        void Drop(Scope scope) {
            for(int i=pending.Count-1;i>=0;i--)if(pending[i].Scope==scope) {Note("drop-invalidated",scope,pending[i].Item.Input);characters-=(pending[i].Item.Input.Text??"").Length;pending.RemoveAt(i);lost=true;}
        }
        public long Resolve(long id,uint pid,ulong root,bool known,bool allowed,string field,ulong tick) {
            Scope scope=scopes.FirstOrDefault(s=>s.Id==id && s.Pid==pid && s.Root==root);
            if(scope==null || !known)return 0;
            if(tick<scope.Start || (scope.End!=0 && tick>=scope.End)) {Drop(scope);return 0;}
            if(scope.Known && !String.IsNullOrEmpty(scope.Field) && !String.IsNullOrEmpty(field) && scope.Field!=field) {
                // A provider changed virtual focus without a delivered event.
                // Its intervening weak text has no reliable field identity.
                for(int i=pending.Count-1;i>=0;i--)if(pending[i].Scope==scope && !pending[i].NativeSafe && !Confirmed(pending[i])) {characters-=(pending[i].Item.Input.Text??"").Length;pending.RemoveAt(i);lost=true;}
                scope.End=tick;
                scope=new Scope {Id=++serial,Pid=pid,Root=root,Focus=scope.Focus,Start=tick};scopes.Add(scope);
            }
            scope.Known=true;scope.Allowed=allowed;scope.Checked=Math.Max(scope.Checked,tick);scope.Field=field;scope.DeferredUntil=0;
            Note("resolve",scope);
            TrimScopes();
            return scope.Id;
        }
        public void Offer(CommittedInput input,string source,uint rootPid,ulong root,bool nativeSafe,string stableField=null) {
            Scope scope=null;
            if(nativeSafe && stableField!=null) {
                scope=scopes.LastOrDefault(s=>s.Pid==rootPid && s.Root==root && s.Focus==input.Window && input.Tick>=s.Start && (s.End==0 || input.Tick<=s.End));
                if(scope==null)scope=new Scope {Id=++serial,Pid=rootPid,Root=root,Focus=input.Window,Known=true,Allowed=true,Field=stableField};
            }
            if(scope==null && nativeSafe && !String.IsNullOrEmpty(input.FieldIdentity)) {
                scope=scopes.LastOrDefault(s=>s.Pid==rootPid && s.Root==root && s.Field==input.FieldIdentity);
                if(scope==null)scope=scopes.LastOrDefault(s=>s.Pid==rootPid && s.Root==root && !s.Known && input.Tick>s.Start && (s.End==0 || input.Tick<s.End));
                if(scope==null)scope=new Scope {Id=++serial,Pid=rootPid,Root=root,Focus=input.Window,Known=true,Allowed=true,Field=input.FieldIdentity};
            }
            if(scope==null)scope=scopes.LastOrDefault(s=>s.Pid==rootPid && s.Root==root && (nativeSafe?input.Tick>=s.Start:input.Tick>s.Start) && (s.End==0 || input.Tick<s.End));
            if(scope==null) {
                // Borderline timestamps across a known focus change are
                // ambiguous; retain no content using the next field's metadata.
                if(nativeSafe && !String.IsNullOrEmpty(input.FieldIdentity))scope=new Scope {Id=++serial,Pid=rootPid,Root=root,Focus=input.Window,Known=true,Allowed=true,Field=input.FieldIdentity};
                else {
                    if(scopes.Any(s=>s.Pid==rootPid && s.Root==root)) {Note("drop-boundary root="+root+" owner="+rootPid,null,input);lost=true;return;}
                    long id=Focus(rootPid,root,input.Window,0,false);scope=scopes.First(s=>s.Id==id);
                }
            }
            int length=(input.Text??"").Length;
            if(scope.Known && !scope.Allowed) {Note("drop-denied",scope,input);return;}
            if(nativeSafe && (input.Flags&1024)!=0 && input.InputSerial!=0) {
                // A safe TSF update can certify its corresponding WM_CHAR in
                // metadata-poor editors, without authorizing other key tokens.
                foreach(var earlier in pending)if(earlier.Scope==scope && !earlier.NativeSafe &&
                    earlier.Item.Input.ProcessId==input.ProcessId && earlier.Item.Input.ThreadId==input.ThreadId &&
                    earlier.Item.Input.Window==input.Window && earlier.Item.Input.InputSerial==input.InputSerial &&
                    (earlier.Item.Input.Kind==1 || earlier.Item.Input.Kind==2))earlier.NativeSafe=true;
            }
            if(nativeSafe && (pending.Count>=8192 || length>500000-characters)) {
                for(int i=0;i<pending.Count && (pending.Count>=8192 || length>500000-characters);) {
                    if(pending[i].NativeSafe) {i++;continue;}
                    characters-=(pending[i].Item.Input.Text??"").Length;pending.RemoveAt(i);lost=true;
                }
            }
            if(pending.Count>=8192 || length>500000-characters) {Note("drop-capacity",scope,input);lost=true;return;}
            pending.Add(new Pending {Scope=scope,NativeSafe=nativeSafe,StableField=stableField,Item=new Item {Input=input,Source=source,Epoch=scope.Id}});characters+=length;
            Note("offer safe="+nativeSafe,scope,input);
        }
        public List<Item> Drain(ulong now,bool finish=false) {
            var ready=new List<Item>();var blocked=new HashSet<string>();
            for(int i=0;i<pending.Count;) {
                Pending next=pending[i];Scope scope=next.Scope;CommittedInput input=next.Item.Input;
                string key=scope.Pid+":"+scope.Root;
                bool denied=scope.Known && !scope.Allowed;
                bool confirmed=scope.Known && scope.Allowed && input.Tick<=scope.Checked;
                bool deferred=scope.End==0 && scope.DeferredUntil!=0 && now<=scope.DeferredUntil;
                bool expired=!next.NativeSafe && !confirmed && input.Kind!=3 && ((now>=input.Tick && now-input.Tick>2500 && !deferred) || finish);
                bool retired=!next.NativeSafe && input.Kind!=3 && scope.End!=0 && !scope.Known;
                bool settled=scope.Known || next.StableField!=null || (input.Flags&24)==24 || (now>=input.Tick && now-input.Tick>=350) || finish;
                bool allowed=(next.NativeSafe && settled) || input.Kind==3 || (scope.Known && scope.Allowed && input.Tick<=scope.Checked);
                if(denied || expired || retired) {
                    Note(denied?"drop-denied":expired?"drop-unconfirmed-expired":"drop-retired-unclassified",scope,input);
                    if(!denied)lost=true;
                } else if(allowed && !blocked.Contains(key)) {
                    next.Item.Field=next.StableField??input.FieldIdentity??scope.Field??"epoch:"+scope.Id;
                    ready.Add(next.Item);
                    Note("ready",scope,input);
                } else {blocked.Add(key);i++;continue;}
                characters-=(input.Text??"").Length;pending.RemoveAt(i);
            }
            return ready;
        }
    }
}
