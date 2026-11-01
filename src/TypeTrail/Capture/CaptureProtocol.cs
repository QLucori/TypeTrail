using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    public sealed class Sample {
        public List<FinalizedInput> Commits;
        public bool CommitOverflow;
        public string TypedText;
        public int Caret = -1;
        public int FieldLength = -1;
        public int TextOffset;
        public int MatchedTyping;
        public long TypedSerial;
        public bool HasCompositionBase;
        public long InputSerial;
        public long FocusSerial;
        public long ProtectionEpoch;
        public ulong ProtectionTick,ProtectionWindow;
        public uint ProtectionPid;
        public bool ProtectionKnown,ProtectionAllowed;
        public string ProtectionField;
        public bool CanInsert;
        public string Diagnostic;
        public int Id;
        public int Epoch;
        public string Key;
        public string Text;
        public string Source;
        public string Status;
        public bool Truncated;
        public bool Active;
        public bool CompositionFinalized;
        public bool ImeCommitHint;
    }
    public sealed class CaptureRequest {
        public bool MetadataOnly;
        public bool DrainOnly;
        public InputActivityStamp[] InputActivity;
        public long ProtectionEpoch;
        public long InputSerial;
        public long FocusSerial;
        public bool CanInsert;
        public bool Diagnostics;
        public int Id;
        public int Epoch;
        public int ParentPid;
        public bool Active;
        public int MaxChars = 10000;
        public string ExcludedApps;
        public bool Flush;
        public long TargetWindow;
        public bool ImeCommitHint;
    }
}
