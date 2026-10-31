using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    internal static class StartupHealth {
        internal static bool ShouldShowExisting(string[] args) {
            return Array.IndexOf(args,"--show")>=0 || Array.IndexOf(args,"--lang")>=0 ||
                (Array.IndexOf(args,"--background")<0 && Array.IndexOf(args,"--write-health")<0);
        }
        internal static Dictionary<string,object> Snapshot(bool paused,bool keyboard,bool native64,bool native32,bool historyReadable,bool listenerError,int pid) {
            bool healthy=historyReadable && (paused || (keyboard && native64 && native32 && !listenerError));
            return new Dictionary<string,object> {
                {"TypeTrailStartupHealth",1},{"Version",typeof(StartupHealth).Assembly.GetName().Version.ToString()},
                {"Pid",pid},{"CheckedUtc",DateTime.UtcNow.ToString("o")},{"Paused",paused},{"KeyboardReady",keyboard},
                {"Native64Ready",native64},{"Native32Ready",native32},{"HistoryReadable",historyReadable},
                {"Healthy",healthy},{"State",healthy?(paused?"paused":"ready"):"not-ready"}
            };
        }
        internal static void Save(string folder,Dictionary<string,object> value) {
            string target=Path.Combine(folder,"startup-health.json"),temp=target+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(value),new UTF8Encoding(false));
            if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
        }
    }
}
