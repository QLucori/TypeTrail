using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TypeTrail {
    // The document is a transient comparison baseline only. Only characters
    // introduced by an attributed input event are projected into disk history.
    internal sealed class InputText {
        public string Snapshot { get; private set; }
        int[] symbols = new int[0];
        bool[] owned = new bool[0];
        public void Reset(string text) {
            Snapshot = text;
            symbols = Split(text);
            owned = new bool[symbols.Length];
        }
        internal static int[] Split(string text) {
            var result = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++) {
                if (Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) result.Add(Char.ConvertToUtf32(text[i], text[++i]));
                else result.Add(text[i]);
            }
            return result.ToArray();
        }
        static void Bounds(int[] before, int[] after, out int prefix, out int suffix) {
            prefix = 0; suffix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        }
        public bool IsOwnedImeReplacement(string next) {
            int[] after = Split(next); int prefix, suffix;
            Bounds(symbols, after, out prefix, out suffix);
            int removed = symbols.Length - prefix - suffix;
            if (removed == 0) return false;
            bool typed = false;
            for (int i = prefix; i < symbols.Length - suffix; i++) {
                int c = symbols[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '\'')) return false;
                typed |= owned[i];
            }
            return typed && after.Skip(prefix).Take(after.Length - prefix - suffix).Any(c => c >= 0x3400 && c <= 0x9FFF);
        }
        public bool Update(string next, bool allowInsertedText, out string input) {
            input = null;
            int[] after = Split(next); int prefix, suffix;
            Bounds(symbols, after, out prefix, out suffix);
            int removed = symbols.Length - prefix - suffix, added = after.Length - prefix - suffix;
            var afterOwned = new bool[after.Length];
            Array.Copy(owned, 0, afterOwned, 0, prefix);
            Array.Copy(owned, symbols.Length - suffix, afterOwned, after.Length - suffix, suffix);
            if (removed == 0 || added == 0) {
                for (int i = prefix; i < after.Length - suffix; i++) afterOwned[i] = allowInsertedText;
            } else {
                // Preserve unchanged original text between disjoint edits too.
                // Ambiguous large rewrites are rebased, never guessed as input.
                if ((long)removed * added > 65536) return false;
                var common = new int[removed + 1, added + 1];
                for (int i = removed - 1; i >= 0; i--)
                    for (int j = added - 1; j >= 0; j--)
                        common[i, j] = symbols[prefix + i] == after[prefix + j] ? 1 + common[i + 1, j + 1] : Math.Max(common[i + 1, j], common[i, j + 1]);
                int oldIndex = 0, newIndex = 0;
                while (newIndex < added) {
                    if (oldIndex < removed && symbols[prefix + oldIndex] == after[prefix + newIndex]) {
                        afterOwned[prefix + newIndex] = owned[prefix + oldIndex]; oldIndex++; newIndex++;
                    } else if (oldIndex < removed && common[oldIndex + 1, newIndex] >= common[oldIndex, newIndex + 1]) oldIndex++;
                    else { afterOwned[prefix + newIndex] = allowInsertedText; newIndex++; }
                }
            }
            symbols = after; owned = afterOwned; Snapshot = next;
            input = Project(); return true;
        }
        public bool ClaimTypedPrefix(string typed, int caret, out string input, out int matched) {
            input = null; matched = 0;
            if (Snapshot == null || String.IsNullOrEmpty(typed)) return false;
            if (caret < 0) caret = Snapshot.Length;
            if (caret > Snapshot.Length) return false;
            // Keep the hint until the whole sequence is visible. A short matching
            // prefix could merely be the final letter of the pre-existing text.
            if (typed.Length > caret || Char.IsHighSurrogate(typed[typed.Length - 1])) return false;
            if (String.CompareOrdinal(Snapshot, caret - typed.Length, typed, 0, typed.Length) != 0) return false;
            int first = Split(Snapshot.Substring(0, caret - typed.Length)).Length;
            int count = Split(typed).Length;
            for (int i = first; i < first + count; i++) owned[i] = true;
            matched = typed.Length; input = Project(); return true;
        }
        string Project() {
            var text = new StringBuilder(); bool gap = false;
            for (int i = 0; i < symbols.Length; i++) {
                if (!owned[i]) { if (text.Length > 0) gap = true; continue; }
                if (gap) { if (text[text.Length - 1] != '\n') text.Append('\n'); gap = false; }
                if (symbols[i] <= 0xFFFF) text.Append((char)symbols[i]); else text.Append(Char.ConvertFromUtf32(symbols[i]));
            }
            return text.ToString();
        }
    }
}
