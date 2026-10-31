using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    public sealed class DiskStore {
        public readonly string Folder;
        public string Error;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        bool compactHistory;
        bool recoveryBlocked;
        string readError,writeError,settingsError;
        public bool HistoryReadPending { get { return recoveryBlocked; } }
        public DiskStore(string folder) { Folder = ProfileIdentity.Normalize(folder); Directory.CreateDirectory(Folder); }
        public Settings LoadSettings() {
            for(int attempt=0;;attempt++) {
                try {var s=json.Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder,"settings.json")));s.Validate();return s;}
                catch(FileNotFoundException){return new Settings();}
                catch(Exception error) {
                    if(attempt<2 && (error is IOException || error is UnauthorizedAccessException)){System.Threading.Thread.Sleep((attempt+1)*50);continue;}
                    throw new IOException(L.T("设置暂时无法读取，原文件未改动，请稍后重试。"),error);
                }
            }
        }
        public void SaveSettings(Settings settings) {
            try { AtomicWrite(Path.Combine(Folder, "settings.json"), Encoding.UTF8.GetBytes(json.Serialize(settings)));if(Error==settingsError)Error=null;settingsError=null; }
            catch { Error = settingsError=L.T("设置暂时无法保存，请检查数据目录。"); }
        }
        public List<Entry> LoadHistory() {
            string path = Path.Combine(Folder, "history.dat");
            byte[] encrypted;
            try { encrypted=File.ReadAllBytes(path); }
            catch(FileNotFoundException) { ReadSucceeded();compactHistory=false;return new List<Entry>(); }
            catch { return ReadFailed(path); }
            try {
                byte[] clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                List<Entry> entries;
                try {
                    compactHistory=HistoryArchiveCodec.IsCompact(clear);
                    entries=(compactHistory?HistoryArchiveCodec.Decode(clear):(json.Deserialize<List<Entry>>(Encoding.UTF8.GetString(clear))??new List<Entry>())).Where(e=>e!=null && e.HasContent).ToList();
                } finally {Array.Clear(clear,0,clear.Length);}
                foreach (var entry in entries) {
                    if(String.IsNullOrEmpty(entry.Id))entry.Id=Guid.NewGuid().ToString("N");
                    if(entry.Source==null)entry.Source="Application";
                    if(entry.Text==null)entry.Text="";
                    entry.Created = entry.Created.ToLocalTime(); entry.Updated = entry.Updated.ToLocalTime();
                    entry.Versions = (entry.Versions ?? new List<Revision>()).Where(v => v != null && !String.IsNullOrEmpty(v.Text)).ToList();
                    foreach (var version in entry.Versions) version.Saved = version.Saved.ToLocalTime();
                }
                ReadSucceeded();return entries.OrderByDescending(e=>e.Updated).ToList();
            } catch { return ReadFailed(path); }
        }
        void ReadSucceeded() {
            recoveryBlocked=false;if(Error==readError)Error=null;readError=null;
        }
        List<Entry> ReadFailed(string path) {
            bool first=!recoveryBlocked;recoveryBlocked=true;
            // A backup is recovery material, never permission to overwrite an
            // unreadable original with a partial in-memory history.
            if(first)try {File.Copy(path,Path.Combine(Folder,"history-unreadable-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+".dat"),false);}catch{}
            Error=readError=L.T("历史暂时无法读取，原文件已保护，正在重试。");
            return new List<Entry>();
        }
        public bool SaveHistory(List<Entry> entries) {
            if(recoveryBlocked){if(Error==null)Error=readError;return false;}
            try {
                byte[] clear=HistoryArchiveCodec.Encode(entries),encrypted;
                try{encrypted=ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser);}finally{Array.Clear(clear,0,clear.Length);}
                if(!compactHistory && File.Exists(Path.Combine(Folder,"history.dat")) && !BackupHistory("before-compact-1.7.4"))return false;
                AtomicWrite(Path.Combine(Folder, "history.dat"), encrypted);
                compactHistory=true;
                if(Error==writeError)Error=null;writeError=null;
                return true;
            } catch { Error = writeError=L.T("历史暂时无法写入磁盘，本次记录仍保留在内存。"); return false; }
        }
        public bool BackupHistory(string reason) {
            try {
                string source = Path.Combine(Folder, "history.dat");
                if (File.Exists(source)) File.Copy(source, Path.Combine(Folder, "history-" + reason + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".dat"), false);
                return true;
            } catch { Error = L.T("旧历史备份失败，已跳过碎片整理。"); return false; }
        }
        public bool ArchiveSnapshotHistory(bool retainHistoryFile = true,List<Entry> retainedInputs=null) {
            if (!File.Exists(Path.Combine(Folder, "history.dat"))) return true;
            if (!BackupHistory("before-input-only-1.3")) return false;
            if (!retainHistoryFile) { try { DeleteHistory(); return true; } catch { Error = L.T("备份已保存，但旧历史未能移出，已暂停记录。"); return false; } }
            return SaveHistory(retainedInputs??new List<Entry>());
        }
        public void DeleteHistory() {
            foreach (string name in new[] { "history.dat", "history.dat.tmp" }) {
                string p = Path.Combine(Folder, name); if (File.Exists(p)) File.Delete(p);
            }
            ReadSucceeded();
        }
        static void AtomicWrite(string path, byte[] bytes) {
            string temp = path + ".tmp";
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}
