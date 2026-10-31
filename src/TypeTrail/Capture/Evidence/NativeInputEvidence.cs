using System;
using System.Text;

namespace TypeTrail {
    internal static class NativeInputEvidence {
        internal static bool SafeForAdmission(CommittedInput input) {
            // A deletion contains no document text to import. Keep it in its
            // own focus epoch even when the editor exposes no UIA metadata;
            // an explicitly denied field is still rejected by FieldAdmission.
            return (input.Kind==2 && String.IsNullOrEmpty(input.Text)) ||
                (input.Flags&1024)!=0 || (input.Flags&24)==24 || ((input.Flags&32)!=0 && input.InputSerial!=0);
        }
        static bool CanonicalEvidence(string value,string typed,out string accepted,out int consumed) {
            accepted=null;consumed=0;
            try {
                string canonicalValue=value.Normalize(NormalizationForm.FormC),canonicalTyped=typed.Normalize(NormalizationForm.FormC);
                if(canonicalValue.IndexOf(canonicalTyped,StringComparison.Ordinal)>=0) {accepted=canonicalTyped;consumed=typed.Length;return true;}
                if(!canonicalTyped.StartsWith(canonicalValue,StringComparison.Ordinal))return false;
                for(int length=1;length<=typed.Length;length++) {
                    if(Char.IsHighSurrogate(typed[length-1]) && length<typed.Length && Char.IsLowSurrogate(typed[length]))continue;
                    string prefix=typed.Substring(0,length).Normalize(NormalizationForm.FormC);
                    if(prefix.Length>canonicalValue.Length)return false;
                    if(prefix==canonicalValue) {accepted=value;consumed=length;return true;}
                }
            } catch(ArgumentException) {}
            return false;
        }
        public static bool ConfirmPaste(Sample sample, string paste, PasteBaseline before, int consumed, out string accepted) {
            accepted=null;
            if(before==null || sample.Status!="ok" || sample.FieldLength<0 || sample.Caret<0 || consumed<0 ||
                String.IsNullOrEmpty(sample.Text) || String.IsNullOrEmpty(paste)) return false;
            string field=History.Normalize(sample.Text),text=History.Normalize(paste);
            long inserted=(long)sample.FieldLength-before.Length+(before.End-before.Start);
            long fullCaret=(long)sample.Caret+sample.TextOffset;
            if(inserted<=consumed || inserted>(long)consumed+text.Length || fullCaret!=(long)before.Start+inserted) return false;
            int count=(int)inserted-consumed;
            long offset=(long)before.Start+consumed-sample.TextOffset;
            int skipped=offset<0?(int)Math.Min(-offset,count):0;
            offset+=skipped;count-=skipped;
            if(count<=0 || offset<0 || offset+count>field.Length || sample.Caret>field.Length) return false;
            if(String.CompareOrdinal(field,(int)offset,text,skipped,count)!=0) return false;
            accepted=text.Substring(skipped,count);return true;
        }
        public static bool Verify(CommittedInput input, string typed, out int consumed) {
            consumed = 0;
            if (input.Kind == 1) input.Text = History.Normalize(input.Text);
            typed = History.Normalize(typed);
            // Plain TSF edit ranges can include unchanged surrounding words or a
            // programmatic update. Save only the matching actual input evidence.
            if ((input.Flags & 4) != 0 && (input.Flags & 8) == 0 && input.Kind == 1) {
                if (String.IsNullOrEmpty(typed) || String.IsNullOrEmpty(input.Text)) return false;
                if (input.Text.IndexOf(typed, StringComparison.Ordinal) >= 0) { input.Text = typed; consumed = typed.Length; return true; }
                if (typed.StartsWith(input.Text, StringComparison.Ordinal)) { consumed = input.Text.Length; return true; }
                // Input captured after an IME commit may follow preedit hints
                // queued in the same coarse clock tick. Recover only a literal
                // newly typed suffix; never import the provider's surrounding text.
                if(!String.IsNullOrWhiteSpace(input.Text) && typed.EndsWith(input.Text,StringComparison.Ordinal)) {consumed=typed.Length;return true;}
                string canonical;
                if(CanonicalEvidence(input.Text,typed,out canonical,out consumed)) {input.Text=canonical;return true;}
                return false;
            }
            if (input.Kind == 1 && !String.IsNullOrEmpty(input.Text) && !String.IsNullOrEmpty(typed) && typed.StartsWith(input.Text, StringComparison.Ordinal)) consumed = input.Text.Length;
            else if(input.Kind==1 && !String.IsNullOrEmpty(input.Text) && !String.IsNullOrEmpty(typed) && typed.EndsWith(input.Text,StringComparison.Ordinal))consumed=typed.Length;
            else if(input.Kind==1 && !String.IsNullOrEmpty(input.Text) && !String.IsNullOrEmpty(typed)) {
                string canonical;int matched;
                if(CanonicalEvidence(input.Text,typed,out canonical,out matched))consumed=matched;
            }
            return true;
        }
    }
}
