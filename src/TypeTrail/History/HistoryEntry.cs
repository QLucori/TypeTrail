using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    public sealed class Entry {
        public string Id = Guid.NewGuid().ToString("N");
        public string Text;
        public string Source;
        public DateTime Created = DateTime.Now;
        public DateTime Updated = DateTime.Now;
        public bool PreviousVersion;
        public bool InputOnly;
        public bool Truncated;
        public List<Revision> Versions = new List<Revision>();
        public bool IsCleared { get { return String.IsNullOrEmpty(Text) && Versions != null && Versions.Any(v => v != null && !String.IsNullOrEmpty(v.Text)); } }
        public bool HasContent { get { return !String.IsNullOrEmpty(Text) || IsCleared; } }
        public List<Revision> GetDisplayVersions() {
            var versions = (Versions ?? new List<Revision>()).Where(v => v != null && !String.IsNullOrEmpty(v.Text)).ToList();
            // Older builds stored shrinking prefixes during full deletion.
            // Collapse those redundant choices without deleting stored history.
            return versions.Where(v => !versions.Any(other => other.Text.Length > v.Text.Length && other.Text.StartsWith(v.Text, StringComparison.Ordinal))).ToList();
        }
        public int CharacterCount { get { return (Text ?? "").Length + (Versions == null ? 0 : Versions.Where(v => v != null).Sum(v => (v.Text ?? "").Length)); } }
        public string Preview { get { return IsCleared ? L.T("已清空") : Text == null ? "" : Text.Replace('\r', ' ').Replace('\n', ' ').Trim(); } }
        public string TimeLabel { get { return Updated.Date == DateTime.Today ? Updated.ToString("HH:mm") : Updated.ToString("MM/dd HH:mm"); } }
        public string Meta { get { return L.Display(Source) + "  ·  " + TimeLabel + (PreviousVersion ? L.T("  ·  修改前") : ""); } }
    }
    public sealed class Revision {
        public string Text;
        public DateTime Saved;
    }
}
