using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    public sealed class History {
        public List<Entry> Entries = new List<Entry>();
        sealed class Session {
            public readonly InputText Input = new InputText();
            public Entry Current;
            public bool Composing;
            public long InputSerial;
            public long FocusSerial;
            public long LastImeSerial;
            public DateTime Seen;
            public DateTime LastRevision = DateTime.MinValue;
            public DateTime LastTyping = DateTime.MinValue;
        }
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        string activeKey;
        public int Limit = 50000;
        public int Characters { get { return Entries.Sum(e => e.CharacterCount); } }
        public void ResetSession() { sessions.Clear(); activeKey = null; }
        public void LeaveField() { activeKey = null; }
        public void Clear() { Entries.Clear(); ResetSession(); }
        public bool Apply(Sample sample) {
            if ((sample.Status != "ok" && sample.Status != "composing") || String.IsNullOrEmpty(sample.Key)) { LeaveField(); return false; }
            Session session;
            if (!sessions.TryGetValue(sample.Key, out session)) {
                if (sessions.Count >= 64) sessions.Remove(sessions.OrderBy(p => p.Value.Seen).First().Key);
                session = new Session(); sessions[sample.Key] = session;
            }
            session.Seen = DateTime.UtcNow;
            bool resumed = activeKey != sample.Key || session.FocusSerial != sample.FocusSerial;
            activeKey = sample.Key;
            bool recentTyping = (DateTime.UtcNow - session.LastTyping).TotalSeconds < 15;
            if (sample.Status == "composing") {
                // TextEdit exposes the original text outside the live composition.
                // Use that as the baseline even if typing began before our first read.
                if (session.Input.Snapshot == null && sample.HasCompositionBase) {
                    session.Input.Reset(Normalize(sample.Text)); session.Current = null;
                    session.FocusSerial = sample.FocusSerial; session.InputSerial = sample.InputSerial - 1;
                    resumed = false;
                }
                if (session.Input.Snapshot == null) { session.Composing = false; activeKey = null; return false; }
                if ((sample.Active && sample.CanInsert) || recentTyping) session.Composing = true;
                session.FocusSerial = sample.FocusSerial;
                if (session.Composing && sample.HasCompositionBase && Normalize(sample.Text) != session.Input.Snapshot) {
                    string committed;
                    if (session.Input.Update(Normalize(sample.Text), false, out committed)) {
                        if (String.IsNullOrWhiteSpace(committed)) {
                            bool removed = session.Current != null && Entries.Remove(session.Current);
                            session.Current = null; return removed;
                        }
                        return SaveInput(session, sample, committed, true);
                    }
                }
                return false;
            }
            string next = Normalize(sample.Text);
            string input;
            bool observedImeReplacement = session.Input.Snapshot != null && recentTyping && session.Input.IsOwnedImeReplacement(next);
            bool finalized = observedImeReplacement;
            if (observedImeReplacement) { resumed = false; session.FocusSerial = sample.FocusSerial; }
            if (resumed || session.Input.Snapshot == null || sample.Truncated) {
                bool changedBaseline = session.Input.Snapshot != next || sample.Truncated;
                bool recoverTyping = sample.Active && sample.CanInsert && !String.IsNullOrEmpty(sample.TypedText);
                if (changedBaseline) {
                    string ignored;
                    // On returning to the same editor, retain ownership of unchanged
                    // typed text, but claim only the observed new keyboard characters.
                    if (!recoverTyping || session.Input.Snapshot == null || !session.Input.Update(next, false, out ignored)) {
                        session.Input.Reset(next); session.Current = null;
                    }
                }
                session.InputSerial = sample.InputSerial; session.FocusSerial = sample.FocusSerial; session.Composing = false;
                if (recoverTyping && session.Input.ClaimTypedPrefix(sample.TypedText, sample.Caret, out input, out sample.MatchedTyping)) return SaveInput(session, sample, input, false);
                sample.Status = sample.Truncated ? "uncertain_input" : "baseline";
                return false;
            }
            if (next == session.Input.Snapshot) {
                session.Composing = false;
                // Also recover a first burst which arrived while the first read was
                // in flight. No document characters other than the key hint are owned.
                if (sample.Active && sample.CanInsert && session.Input.ClaimTypedPrefix(sample.TypedText, sample.Caret, out input, out sample.MatchedTyping)) {
                    session.InputSerial = sample.InputSerial;
                    return SaveInput(session, sample, input, false);
                }
                return false;
            }
            bool freshInput = sample.Active && sample.InputSerial > session.InputSerial;
            if (freshInput && sample.CanInsert) session.LastTyping = DateTime.UtcNow;
            bool fallbackCommit = sample.Active && sample.ImeCommitHint && sample.CanInsert && sample.InputSerial >= session.InputSerial && sample.InputSerial > session.LastImeSerial && session.Input.IsOwnedImeReplacement(next);
            finalized |= session.Composing || fallbackCommit;
            session.Composing = false;
            if (!freshInput && !finalized) {
                // A slow provider can publish the final letters after the read
                // which consumed their input serial. Recover only matching typed
                // evidence, retaining the original-text ownership boundary.
                if (sample.Active && sample.CanInsert && !String.IsNullOrEmpty(sample.TypedText)) {
                    string ignored;
                    if (session.Input.Update(next, false, out ignored) && session.Input.ClaimTypedPrefix(sample.TypedText, sample.Caret, out input, out sample.MatchedTyping)) return SaveInput(session, sample, input, false);
                }
                // Passive page refreshes, autofill and incoming messages become a new
                // unowned baseline; they must not modify a previously recorded card.
                session.Input.Reset(next); session.Current = null; session.InputSerial = Math.Max(session.InputSerial, sample.InputSerial);
                sample.Status = "baseline"; return false;
            }
            bool projected = session.Input.Update(next, (freshInput && sample.CanInsert) || finalized, out input);
            session.InputSerial = Math.Max(session.InputSerial, sample.InputSerial);
            if (finalized) session.LastImeSerial = sample.InputSerial;
            if (!projected) { session.Input.Reset(next); session.Current = null; sample.Status = "uncertain_input"; return false; }
            if (freshInput && sample.CanInsert && !finalized) {
                string verified;
                if (session.Input.ClaimTypedPrefix(sample.TypedText, sample.Caret, out verified, out sample.MatchedTyping)) input = verified;
            }
            return SaveInput(session, sample, input, finalized);
        }
        bool SaveInput(Session session, Sample sample, string input, bool finalized) {
            if (sample.Status != "composing") sample.Status = "ok";
            if (sample.Active && sample.CanInsert) session.LastTyping = DateTime.UtcNow;
            if (!String.IsNullOrEmpty(sample.TypedText) && sample.MatchedTyping == sample.TypedText.Length) session.InputSerial = Math.Max(session.InputSerial, sample.TypedSerial);
            if (String.IsNullOrWhiteSpace(input)) { session.Current = null; session.LastRevision = DateTime.MinValue; return false; }
            Entry current = session.Current;
            if (current != null && !Entries.Contains(current)) current = session.Current = null;
            if (current != null && current.Text == input) return false;
            if (current != null && !finalized && RemovedText(current.Text, input) && (DateTime.UtcNow - session.LastRevision).TotalSeconds > 2) {
                if (current.Versions == null) current.Versions = new List<Revision>();
                if (!current.Versions.Any(v => v.Text == current.Text)) current.Versions.Insert(0, new Revision { Text = current.Text, Saved = current.Updated });
                current.Versions = current.Versions.Take(8).ToList();
                session.LastRevision = DateTime.UtcNow;
            }
            if (current == null) {
                current = new Entry { Text = input, Source = sample.Source, InputOnly = true };
                session.Current = current;
            }
            current.Text = input; current.Updated = DateTime.Now;
            Entries.Remove(current); Entries.Insert(0, current);
            Trim(); return true;
        }
        public int CompactLegacyFragments() {
            var removed = new List<Entry>();
            foreach (Entry older in Entries.Where(e => e.PreviousVersion).ToList()) {
                Entry completed = Entries.FirstOrDefault(e => e != older && !e.PreviousVersion && e.Source == older.Source && e.Updated >= older.Updated && (e.Updated - older.Updated).TotalMinutes <= 5 && LegacyFragmentOf(older.Text, e.Text));
                if (completed != null) removed.Add(older);
            }
            foreach (var entry in removed) Entries.Remove(entry);
            return removed.Count;
        }
        static bool LegacyFragmentOf(string fragment, string completed) {
            if (fragment == completed) return true;
            int prefix = 0;
            while (prefix < fragment.Length && prefix < completed.Length && fragment[prefix] == completed[prefix]) prefix++;
            if (prefix < 8 || !fragment.Substring(0, prefix).Any(IsHan)) return false;
            if (prefix == fragment.Length) return true;
            string tail = fragment.Substring(prefix);
            return tail.Length <= 80 && tail.Contains("'") && tail.All(c => (c >= 'a' && c <= 'z') || c == '\'') && completed.Substring(prefix).Any(IsHan);
        }
        static bool IsHan(char c) { return c >= '\u3400' && c <= '\u9FFF'; }
        public void Trim() {
            int used = 0,write=0;
            for(int index=0;index<Entries.Count;index++) {
                if(used>=Limit)break;
                Entry e=Entries[index];
                if(e==null || !e.HasContent)continue;
                if(e.Text==null)e.Text="";
                int available = Limit - used;
                if (e.Text.Length > available) { e.Text = Tail(e.Text, available); e.Truncated = true; }
                used+=e.Text.Length;
                if(e.Versions==null)e.Versions=new List<Revision>();
                int revisionWrite=0;
                for(int revisionIndex=0;revisionIndex<e.Versions.Count && revisionWrite<8;revisionIndex++) {
                    var revision=e.Versions[revisionIndex];
                    if(revision==null || String.IsNullOrEmpty(revision.Text))continue;
                    if(revision.Text.Length<=Limit-used){e.Versions[revisionWrite++]=revision;used+=revision.Text.Length;}
                }
                if(revisionWrite<e.Versions.Count)e.Versions.RemoveRange(revisionWrite,e.Versions.Count-revisionWrite);
                if(e.HasContent)Entries[write++]=e;
            }
            if(write<Entries.Count)Entries.RemoveRange(write,Entries.Count-write);
        }
        public static string Normalize(string s) { return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\0'); }
        public static string Tail(string s, int cap) {
            if (s.Length <= cap) return s;
            int start = s.Length - cap;
            if (start < s.Length && Char.IsLowSurrogate(s[start])) start++;
            return s.Substring(start);
        }
        static bool RemovedText(string oldText, string newText) {
            int prefix = 0, suffix = 0;
            while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
            while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
            return oldText.Length - prefix - suffix > 0;
        }
        static bool IsImeReplacement(string oldText, string newText) {
            int prefix = 0, suffix = 0;
            while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
            while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
            string removed = oldText.Substring(prefix, oldText.Length - prefix - suffix);
            string added = newText.Substring(prefix, newText.Length - prefix - suffix);
            // This only merges observed replacement text after an IME commit key.
            // It never guesses Chinese from pinyin or treats English apostrophes as composition.
            return removed.Length > 0 && removed.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '\'') && added.Any(IsHan);
        }
    }
}
