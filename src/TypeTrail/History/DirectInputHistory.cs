using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TypeTrail {
    // A journal of committed characters. It never reads a document or imports
    // the text which was present before this input session.
    internal sealed class DirectInputHistory {
        readonly History history;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        sealed class Observation {
            public uint Family,Serial,Thread;
            public readonly StringBuilder Text=new StringBuilder();
            public int Offset;
            public ulong Tick;
            public int Length {get{return Text.Length-Offset;}}
            public bool Prefix(string value,int start,int count) {
                if(count>Length || start+count>value.Length)return false;
                for(int i=0;i<count;i++)if(Text[Offset+i]!=value[start+i])return false;
                return true;
            }
        }
        sealed class ClosedObservation {public Observation Value;public ulong Boundary;}
        sealed class Session { public Entry Entry; public ulong LastTick; public Revision DeletionVersion; public int Cursor=-1,Anchor=-1,Length=-1; public string Leading = ""; public readonly List<Observation> Unpaired = new List<Observation>(); public readonly List<ClosedObservation> Closed=new List<ClosedObservation>(); }
        static void SaveBeforeEdit(Session session) {
            var entry=session.Entry;if(entry==null || String.IsNullOrEmpty(entry.Text) || session.DeletionVersion!=null)return;
            session.DeletionVersion=entry.Versions.FirstOrDefault(v=>v.Text==entry.Text)??new Revision {Text=entry.Text,Saved=entry.Updated};
            if(!entry.Versions.Contains(session.DeletionVersion))entry.Versions.Insert(0,session.DeletionVersion);
        }
        void FinishDeletion(Session session,Entry entry) {
            // The complete pre-edit draft may temporarily exceed the budget.
            // Retry that same snapshot as text shrinks; never replace it with
            // a shrinking prefix, and keep both deletion directions ordered.
            if(session.DeletionVersion!=null && !entry.Versions.Contains(session.DeletionVersion))entry.Versions.Insert(0,session.DeletionVersion);
            entry.Updated=DateTime.Now;
            history.Entries.Remove(entry);history.Entries.Insert(0,entry);
            session.Length=entry.Text.Length;history.Trim();
        }
        bool EditCommand(Session session,CommittedInput input) {
            var entry=session.Entry;if(entry==null)return false;
            int key=(int)((input.Flags>>17)&255);bool ctrl=(input.Flags&(1u<<25))!=0,shift=(input.Flags&(1u<<26))!=0;
            if(session.Cursor<0)session.Cursor=session.Anchor=entry.Text.Length;
            int start=Math.Min(session.Cursor,session.Anchor),end=Math.Max(session.Cursor,session.Anchor);
            if(ctrl && key==65){session.Anchor=0;session.Cursor=entry.Text.Length;return false;}
            if(!ctrl && (key==37 || key==39)) {
                int next;
                if(!shift && start!=end)next=key==37?start:end;
                else if(key==37 && session.Cursor>0)next=GraphemeText.LastStart(entry.Text.Substring(0,session.Cursor));
                else if(key==39 && session.Cursor<entry.Text.Length){next=entry.Text.Length;int cursor=session.Cursor;GraphemeText.Scan(entry.Text,delegate(int boundary){if(boundary>cursor && boundary<next)next=boundary;});}
                else {return RetireFragment(session);} // Movement outside owned text cannot be reconstructed.
                session.Cursor=next;if(!shift)session.Anchor=next;return false;
            }
            if(!ctrl && key==46) {
                if(start==end && end<entry.Text.Length){end=entry.Text.Length;int cursor=start;GraphemeText.Scan(entry.Text,delegate(int boundary){if(boundary>cursor && boundary<end)end=boundary;});}
                if(start==end)return false;
                SaveBeforeEdit(session);entry.Text=entry.Text.Remove(start,end-start);session.Cursor=session.Anchor=start;
                FinishDeletion(session,entry);return true;
            }
            return RetireFragment(session);
        }
        static bool RetireFragment(Session session) {
            bool changed=session.Entry!=null && !session.Entry.IsCleared && !session.Entry.PreviousVersion;
            if(changed)session.Entry.PreviousVersion=true;
            Retire(session);return changed;
        }
        static void Retire(Session session) {session.Entry=null;session.DeletionVersion=null;session.Cursor=session.Anchor=-1;session.Length=-1;session.Leading="";session.Unpaired.Clear();}
        public DirectInputHistory(History value) { history = value; }
        public void Reset() { sessions.Clear(); }
        static string Key(uint pid,ulong window,string field) {return pid+":"+window+"/"+(field??"");}
        public bool RebindField(uint pid,ulong window,string previous,string current) {
            string oldKey=Key(pid,window,previous),newKey=Key(pid,window,current);Session value;
            if(oldKey==newKey || !sessions.TryGetValue(oldKey,out value))return true;
            if(sessions.ContainsKey(newKey))return false;
            sessions.Remove(oldKey);sessions[newKey]=value;return true;
        }
        static bool Nearby(ulong a,ulong b) {return (a>=b?a-b:b-a)<700;}
        static string UniqueText(Session session,CommittedInput input,uint family,string text) {
            session.Closed.RemoveAll(o=>input.Tick>=o.Boundary && input.Tick-o.Boundary>5000);
            int delayed=0;
            if(family==16 && input.OriginTick!=0) {
                while(delayed<text.Length) {
                    int closedAlias=session.Closed.FindIndex(o=>input.OriginTick<o.Boundary && input.OriginTick<=o.Value.Tick && o.Value.Tick<=o.Boundary &&
                        o.Value.Prefix(text,delayed,Math.Min(o.Value.Length,text.Length-delayed)));
                    if(closedAlias<0)break;
                    var value=session.Closed[closedAlias].Value;int count=Math.Min(value.Length,text.Length-delayed);value.Offset+=count;delayed+=count;
                    if(value.Length==0)session.Closed.RemoveAt(closedAlias);
                }
                if(delayed>0)return text.Substring(delayed);
            }
            session.Unpaired.RemoveAll(o=>!Nearby(input.Tick,o.Tick));
            // A nonzero token identifies the same actual input activity. Pair
            // equal prefixes one-to-one even when providers split it differently.
            int consumed=0;
            if(input.InputSerial!=0) {
                while(consumed<text.Length) {
                    int found=session.Unpaired.FindIndex(o=>o.Family!=family && o.Serial==input.InputSerial && o.Thread==input.ThreadId && o.Prefix(text,consumed,Math.Min(o.Length,text.Length-consumed)));
                    if(found<0)break;
                    var old=session.Unpaired[found];int count=Math.Min(old.Length,text.Length-consumed);
                    consumed+=count;old.Offset+=count;
                    if(old.Length==0)session.Unpaired.RemoveAt(found);
                }
            }
            if(consumed>0)return text.Substring(consumed);
            // Legacy/tokenless sources retain whole-observation matching. A
            // tokened producer's consecutive chunks are assembled below, so an
            // external whole-word finalization can still pair with that word.
            int alias=session.Unpaired.FindIndex(o=>o.Family!=family &&
                (o.Serial==0 || input.InputSerial==0 || (o.Serial==input.InputSerial && o.Thread==input.ThreadId)) &&
                o.Length==text.Length && o.Prefix(text,0,text.Length));
            if(alias>=0) {session.Unpaired.RemoveAt(alias);return "";}
            return text;
        }
        static void Remember(Session session,CommittedInput input,uint family,string text) {
            Observation last=session.Unpaired.LastOrDefault();
            if(last==null || input.InputSerial==0 || last.Serial!=input.InputSerial || last.Thread!=input.ThreadId || last.Family!=family) {
                if(session.Unpaired.Count>=32)session.Unpaired.RemoveAt(0);
                last=new Observation {Family=family,Serial=input.InputSerial,Thread=input.ThreadId};session.Unpaired.Add(last);
            }
            last.Tick=input.Tick;last.Text.Append(text);
            // Bound retained aliases even if a malformed provider reuses a token.
            if(last.Text.Length>500000) {
                int remove=last.Text.Length-500000;
                if(Char.IsLowSurrogate(last.Text[remove]) && Char.IsHighSurrogate(last.Text[remove-1]))remove++;
                last.Text.Remove(0,remove);last.Offset=Math.Max(0,last.Offset-remove);
            }
        }
        public bool Apply(CommittedInput input, string source) {
            // Native/TSF and UIA can report different callback thread IDs for
            // the same editor window. They still belong to one input journal.
            string key = Key(input.ProcessId,input.Window,input.FieldIdentity);
            Session session;
            if (!sessions.TryGetValue(key, out session)) {
                if (sessions.Count >= 64) sessions.Remove(sessions.OrderBy(p => p.Value.LastTick).First().Key);
                session = new Session(); sessions[key] = session;
            }
            // Removed or trimmed entries cannot be edited; retain alias evidence for delayed duplicate callbacks.
            if(session.Entry!=null && !history.Entries.Contains(session.Entry)) {session.Entry=null;session.DeletionVersion=null;}
            if(session.Entry!=null && session.Length>=0 && session.Entry.Text.Length<session.Length) {
                int removed=session.Length-session.Entry.Text.Length;
                if(session.Cursor>=0)session.Cursor=Math.Max(0,session.Cursor-removed);
                if(session.Anchor>=0)session.Anchor=Math.Max(0,session.Anchor-removed);
                session.Length=session.Entry.Text.Length;
            }
            if (input.Kind == 3) {
                if((input.Flags&65536)!=0){session.Unpaired.Clear();session.Closed.Clear();return EditCommand(session,input);}
                // Submission may follow a pause. Preserve its already observed
                // native word before expiring the short active pairing window,
                // so a delayed pre-submit UIA final can pair with it once.
                foreach(var observation in session.Unpaired)if(observation.Family!=16)session.Closed.Add(new ClosedObservation {Value=observation,Boundary=input.Tick});
                if(session.Closed.Count>32)session.Closed.RemoveRange(0,session.Closed.Count-32);
                Retire(session); return false;
            }
            foreach(var other in sessions.Values)if(input.Tick>=other.LastTick && input.Tick-other.LastTick>=700)other.Unpaired.Clear();
            // Idle time separates a new insertion into another card, but does
            // not undo ownership of this editor's existing captured draft.
            // A delayed Backspace must still remove its actual last character.
            if (session.Entry != null && (!history.Entries.Contains(session.Entry) || (input.Kind == 1 && input.Tick >= session.LastTick && input.Tick - session.LastTick > 90000))) { session.Entry = null; session.DeletionVersion = null; }
            session.LastTick = Math.Max(session.LastTick, input.Tick);
            if (input.Kind == 2) {
                session.Unpaired.Clear();
                session.Closed.Clear();
                Entry current = session.Entry;
                if (current == null || current.Text.Length == 0) {
                    if (session.Leading.Length > 0) session.Leading = GraphemeText.RemoveLast(session.Leading);
                    return false;
                }
                int cursor=session.Cursor<0?current.Text.Length:Math.Min(session.Cursor,current.Text.Length);
                int anchor=session.Anchor<0?cursor:Math.Min(session.Anchor,current.Text.Length);
                int end=Math.Max(cursor,anchor),last=Math.Min(cursor,anchor);
                if(last==end){if(last==0)return false;last=GraphemeText.LastStart(current.Text.Substring(0,last));}
                // Save the draft before a deletion run, not its shrinking
                // prefixes. New accepted text starts the next editing run.
                if (session.DeletionVersion == null) {
                    session.DeletionVersion = current.Versions.FirstOrDefault(v => v.Text == current.Text)
                        ?? new Revision { Text = current.Text, Saved = current.Updated };
                    if (!current.Versions.Contains(session.DeletionVersion)) current.Versions.Insert(0, session.DeletionVersion);
                }
                string next = current.Text.Remove(last,end-last);
                current.Text = next;
                session.Cursor=session.Anchor=last;
                FinishDeletion(session,current);return true;
            }
            string text = History.Normalize(input.Text);
            if (input.Kind != 1 || text.Length == 0) return false;
            uint family = (input.Flags & 32) != 0 ? 32u : (input.Flags & 16) != 0 ? 16u : (input.Flags & 2) != 0 ? 2u : (input.Flags & 4) != 0 ? 4u : 1u;
            // Match duplicate observations one-to-one. Reusing the last native
            // word could swallow a second genuine word received only via UIA.
            text=UniqueText(session,input,family,text);
            if(text.Length==0)return false;
            Remember(session,input,family,text);
            if(session.Entry!=null && session.Cursor>=0 && session.Anchor>=0 && session.Cursor!=session.Anchor)SaveBeforeEdit(session);
            if (session.Entry == null) {
                if (String.IsNullOrWhiteSpace(text)) { session.Leading = History.Tail(session.Leading + text, 2048); return false; }
                session.Entry = new Entry { Source = source, Text = session.Leading, InputOnly = true }; session.Leading = "";
                session.Cursor=session.Anchor=-1;session.Length=-1;
            }
            int insert=session.Cursor<0?session.Entry.Text.Length:Math.Min(session.Cursor,session.Entry.Text.Length);
            int from=session.Anchor<0?insert:Math.Min(insert,session.Anchor),to=session.Anchor<0?insert:Math.Max(insert,session.Anchor);
            session.Entry.Text=session.Entry.Text.Remove(from,to-from).Insert(from,text);
            session.Cursor=session.Anchor=from+text.Length;session.DeletionVersion=null;session.Entry.Updated = DateTime.Now;
            history.Entries.Remove(session.Entry); history.Entries.Insert(0, session.Entry);
            session.Length=session.Entry.Text.Length;history.Trim();
            // A newline may be Shift+Enter or part of a paste. The native
            // boundary event, rather than the character, marks submission.
            return true;
        }
    }
}
