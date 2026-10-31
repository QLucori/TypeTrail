using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeTrail {
    // One controller owns the state; this file contains its history responsibilities.
    internal sealed partial class Controller {
        void DeleteSelected() {
            var entry = Find<ListBox>("HistoryList").SelectedItem as Entry;
            if (entry == null) return;
            deleted = entry; history.Entries.Remove(entry); history.ResetSession(); epoch++; dirty = true; RefreshList(); Notify(L.T("已删除 · Ctrl + Z 可撤销"));
        }
        void ClearHistory() {
            if ((DateTime.UtcNow - clearArmed).TotalSeconds > 4) { clearArmed = DateTime.UtcNow; Find<Button>("ClearButton").Content = L.T("再点一次确认清空"); return; }
            historyRestoreCancelled=true;
            DiscardCaptureForClear();
            epoch++; lastInput = 0; history.Clear(); directHistory.Reset(); deleted = null; dirty = false;
            pendingPaste=null; pendingPasteSerial=-1; pasteHints.Reset();
            try { disk.DeleteHistory(); } catch { Find<TextBlock>("SettingsStatus").Text = L.T("内存已清空，但磁盘文件删除失败。"); return; }
            historyRestoreCancelled=false;
            Find<Button>("ClearButton").Content = L.T("清空所有历史"); Find<TextBlock>("SettingsStatus").Text = L.T("历史已清空。"); RefreshList();
            WriteHistoryState(true);
            if(!settings.Paused) StartWorker();
        }
        void DiscardCaptureForClear() {
            // Clear is a capture boundary too: neither a native tail nor an
            // already queued worker IME finalization may resurrect old history.
            if(keyboardMonitor!=null){keyboardMonitor.Enabled=false;keyboardMonitor.Clear();}
            nativeCapture.Dispose();
            nativeCapture.Drain(delegate(CommittedInput input,string sourceName) {});
            StopWorker();
            typing.Clear(); retiredTyping.Clear(); pendingPaste=null;
            ResetFieldAdmission();
            pendingPasteSerial=-1; pasteHints.Reset();
            nextPasteBaseline=null;assemblingPasteBaseline=null;pendingPasteBaseline=null;pendingPasteConsumed=0;
            protectedWindow=IntPtr.Zero; inputCanInsert=false;
            if(!settings.Paused)PrepareCapture();
            if(keyboardMonitor!=null)keyboardMonitor.Enabled=!settings.Paused;
        }
        void SaveHistory() {
            lastSave = DateTime.UtcNow;
            if (!settings.RememberHistory) { dirty = false; return; }
            if(migrationBlocked)return;
            if (disk.SaveHistory(history.Entries)) {dirty = false;lastSuccessfulHistorySave=DateTime.UtcNow;}
            else if (window != null && window.IsVisible) Notify(disk.Error);
            WriteHistoryState(true);
        }
        List<Entry> ValidateLoadedHistory(List<Entry> loaded) {
            var valid=loaded.Where(e=>e.InputOnly).ToList();
            if(valid.Count==loaded.Count && settings.RememberHistory) {
                // A missing/stale preference marker does not make confirmed
                // input into an old document snapshot.
                if(settings.HistoryFormat<3){settings.HistoryFormat=3;disk.SaveSettings(settings);}
                return valid;
            }
            if(settings.HistoryFormat<3 || loaded.Any(e=>!e.InputOnly)) {
                int archived=loaded.Count-valid.Count;
                if(disk.ArchiveSnapshotHistory(settings.RememberHistory,valid)) {
                    settings.HistoryFormat=3;
                    disk.SaveSettings(settings);
                    if(archived>0)pendingNotice=L.T("已备份并移出 ")+archived+L.T(" 条旧版快照；现在只记录新增输入");
                }else{migrationBlocked=true;settings.Paused=true;pendingNotice=L.T("旧历史备份未完成，已暂停记录；原文件保持不变");}
                return valid;
            }
            return valid;
        }
        void RetryHistoryLoad() {
            if(!disk.HistoryReadPending || !settings.RememberHistory || migrationBlocked || historyRestoreCancelled || DateTime.UtcNow<nextHistoryRetry)return;
            historyRetryCount++;nextHistoryRetry=DateTime.UtcNow.AddSeconds(Math.Min(30,1<<Math.Min(historyRetryCount,5)));
            var loaded=disk.LoadHistory();
            if(disk.HistoryReadPending){WriteHistoryState();return;}
            recordsAtLoad=loaded.Count;var restored=ValidateLoadedHistory(loaded);
            if(migrationBlocked){if(keyboardMonitor!=null)keyboardMonitor.Enabled=false;nativeCapture.Dispose();StopWorker();}
            var seen=new HashSet<string>(StringComparer.Ordinal);
            history.Entries=history.Entries.Concat(restored).Where(e=>seen.Add(e.Id)).OrderByDescending(e=>e.Updated).ToList();
            history.Trim();listDirty=true;
            if(dirty && !migrationBlocked)SaveHistory();
            if(window!=null)RefreshList();
            Notify(migrationBlocked?pendingNotice:L.T("历史已恢复"));WriteHistoryState(true);
        }
        void WriteHistoryState(bool force=false) {
            if(!force && DateTime.UtcNow<nextHistoryState)return;
            nextHistoryState=DateTime.UtcNow.AddSeconds(1);
            int visible=-1;bool filter=false;
            if(window!=null){visible=Find<ListBox>("HistoryList").Items.Count;filter=Find<TextBox>("SearchBox").Text.Trim().Length>0;}
            HistoryState.Save(disk.Folder,pid,recordsAtLoad,history.Entries.Count,visible,filter,disk.HistoryReadPending,dirty,lastSuccessfulHistorySave,initialHistoryReadFailed,historyRetryCount,firstShownRecords,firstShownFilter);
        }
    }
}
