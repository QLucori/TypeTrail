using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace TypeTrail {
    // Operational counters only: no input text, source names, IDs or search text.
    internal static class HistoryState {
        internal static void Save(string folder,int pid,int loaded,int memory,int visible,bool filter,bool restoring,bool unsaved,DateTime saved,bool initialReadFailed,int retryCount,int firstShown,bool firstFilter) {
            var value=new {HistoryState=1,Version=typeof(HistoryState).Assembly.GetName().Version.ToString(),Pid=pid,
                CheckedUtc=DateTime.UtcNow.ToString("o"),Restoring=restoring,RecordsAtLoad=loaded,RecordsInMemory=memory,
                VisibleRecords=visible,FilterActive=filter,SavePending=unsaved,LastSuccessfulSaveUtc=saved==DateTime.MinValue?null:saved.ToString("o"),
                InitialReadFailed=initialReadFailed,RestoreAttempts=retryCount,FirstShownRecords=firstShown,FirstShownFilterActive=firstFilter};
            string target=Path.Combine(folder,"history-state.json"),temp=target+".tmp";
            try {File.WriteAllText(temp,new JavaScriptSerializer().Serialize(value),new UTF8Encoding(false));
                if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
            }catch{} // Diagnostic failure must not interrupt capture or storage.
        }
    }
}
