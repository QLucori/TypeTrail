using System;
using System.Collections.Generic;
using System.Linq;

namespace TypeTrail {
    public sealed class InputActivityStamp {public ulong Tick;public long Timestamp;}
    internal sealed class KeyboardActivityLedger {
        sealed class Entry {public uint Pid;public ulong Root,Focus,Tick;public long Epoch,Timestamp;public string Field;}
        readonly List<Entry> entries=new List<Entry>();
        public void Clear() {entries.Clear();}
        public void Record(uint pid,ulong root,ulong focus,long epoch,string field,ulong tick,long timestamp=0) {
            if(pid==0 || root==0 || focus==0 || tick==0)return;
            entries.RemoveAll(e=>tick>=e.Tick && tick-e.Tick>30000);
            if(entries.Count>=512)entries.RemoveAt(0);
            entries.Add(new Entry {Pid=pid,Root=root,Focus=focus,Epoch=epoch,Field=field,Tick=tick,Timestamp=timestamp});
        }
        static string Field(Entry entry,Func<long,string> lookup) {return lookup==null?entry.Field:lookup(entry.Epoch)??entry.Field;}
        public InputActivityStamp[] ForRequest(uint pid,ulong root,ulong focus,long epoch,string field,ulong now,Func<long,string> lookup,Func<long,bool> denied) {
            return entries.Where(e=>e.Pid==pid && e.Root==root && e.Focus==focus && e.Tick<=now && now-e.Tick<=5000 &&
                (denied==null || !denied(e.Epoch)) && (field!=null && Field(e,lookup)!=null?Field(e,lookup)==field:e.Epoch==epoch)).Select(e=>new InputActivityStamp {Tick=e.Tick,Timestamp=e.Timestamp}).ToArray();
        }
        public bool Has(uint pid,ulong root,ulong focus,string field,ulong before,Func<long,string> lookup,Func<long,bool> denied,long beforeTimestamp=0) {
            return entries.Any(e=>e.Pid==pid && e.Root==root && e.Focus==focus && e.Tick<=before && (beforeTimestamp==0 || e.Timestamp<=beforeTimestamp) &&
                (denied==null || !denied(e.Epoch)) && (field==null || Field(e,lookup)==null || Field(e,lookup)==field));
        }
        public void Consume(uint pid,ulong root,ulong focus,string field,long epoch,ulong before,Func<long,string> lookup,long beforeTimestamp=0,bool inclusive=true) {
            bool native=field!=null && field.StartsWith("native:",StringComparison.Ordinal);
            entries.RemoveAll(e=>e.Pid==pid && e.Root==root && e.Focus==focus && (inclusive?e.Tick<=before:e.Tick<before) && (beforeTimestamp==0 || e.Timestamp<=beforeTimestamp) &&
                (native || e.Epoch==epoch || (field!=null && Field(e,lookup)==field)));
        }
    }
}
