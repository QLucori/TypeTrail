using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class SetupHelper {
    // Uses the app's existing per-profile exit signal. No keystrokes or history are read.
    static int Stop(string folder, string executable) {
        // The profile mutex can be released before CLR/native teardown finishes.
        // Retain handles to our installed processes and wait for actual exit too.
        executable = Path.GetFullPath(executable);
        if (!String.Equals(Path.GetFileName(executable), "TypeTrail.exe", StringComparison.OrdinalIgnoreCase)) return 2;
        var processes = new List<Process>();
        foreach (var candidate in Process.GetProcessesByName("TypeTrail")) {
            try {
                if (String.Equals(candidate.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    processes.Add(candidate);
                else candidate.Dispose();
            } catch { candidate.Dispose(); }
        }
        try { return StopProfile(folder, processes); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static int StopProfile(string folder, List<Process> processes) {
        string key = TypeTrail.ProfileIdentity.Key(folder);
        Mutex mutex;
        try { mutex = Mutex.OpenExisting(key); }
        catch (WaitHandleCannotBeOpenedException) { return WaitForProcesses(processes); }
        using (mutex) {
            // Startup can briefly precede creation of the exit signal.
            bool signalled = false;
            for (int i = 0; i < 30 && !signalled; i++) {
                try { using (var ev = EventWaitHandle.OpenExisting(key + "-exit")) ev.Set(); signalled = true; }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
            }
            if (!signalled) return 10;
            bool acquired;
            try { acquired = mutex.WaitOne(12000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 11;
            mutex.ReleaseMutex();
        }
        return WaitForProcesses(processes);
    }

    static int WaitForProcesses(List<Process> processes) {
        var deadline = Stopwatch.StartNew();
        foreach (var process in processes) {
            try {
                int remaining = Math.Max(0, 12000 - (int)deadline.ElapsedMilliseconds);
                if (!process.WaitForExit(remaining)) return 12;
            } catch (InvalidOperationException) { }
        }
        return 0;
    }

    static int RemoveStartup(string shortcutPath, string executable) {
        if (!File.Exists(shortcutPath)) return 0;
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType), shortcut = null;
        try {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            string target = (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
            if (String.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                File.Delete(shortcutPath);
        } finally {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        return 0;
    }

    [STAThread]
    static int Main(string[] args) {
        try {
            if (args.Length == 3 && args[0] == "stop") return Stop(args[1], args[2]);
            if (args.Length == 3 && args[0] == "remove-startup") return RemoveStartup(args[1], args[2]);
            return 2;
        } catch { return 20; }
    }
}
