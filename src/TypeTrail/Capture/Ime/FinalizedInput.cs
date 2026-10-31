using System;
using System.Collections.Generic;
using System.Text;

namespace TypeTrail {
    // A finalized IME event carries the editor that owned its subscription,
    // rather than borrowing the foreground editor at the next polling read.
    public sealed class FinalizedInput {
        public uint ProcessId, ThreadId;
        public ulong Window, Tick,OriginTick;
        public long Timestamp,OriginTimestamp;
        public string Text, Source, ProcessName, FieldIdentity,ActivityField;
        internal static FinalizedInput Capture(FinalizedInput binding, string text) {
            return new FinalizedInput {
                ProcessId = binding == null ? 0 : binding.ProcessId,
                ThreadId = binding == null ? 0 : binding.ThreadId,
                Window = binding == null ? 0 : binding.Window,
                Source = binding == null ? null : binding.Source,
                ProcessName = binding == null ? null : binding.ProcessName,
                FieldIdentity = binding == null ? null : binding.FieldIdentity,
                ActivityField = binding == null ? null : binding.FieldIdentity,
                Text = text, Tick = Native.GetTickCount64()
            };
        }
    }

    // Shared by current and retiring subscriptions. A callback admitted before
    // unsubscription can finish afterwards without losing or renaming its text.
    internal sealed class FinalizedInputQueue {
        const int MaxCommits = 2048, MaxCharacters = 500000;
        readonly object sync = new object();
        readonly Queue<FinalizedInput> pending = new Queue<FinalizedInput>();
        int characters;
        volatile bool overflowed;
        public bool Overflowed { get { return overflowed; } }
        public void ReportOverflow() { overflowed = true; }
        public void Append(FinalizedInput item) {
            if (item == null || String.IsNullOrEmpty(item.Text)) return;
            lock (sync) {
                if (pending.Count >= MaxCommits || item.Text.Length > MaxCharacters - characters) { overflowed = true; return; }
                pending.Enqueue(item); characters += item.Text.Length;
            }
        }
        public List<FinalizedInput> Take() {
            lock (sync) {
                var result = new List<FinalizedInput>(pending);
                pending.Clear(); characters = 0; return result;
            }
        }
        public void Clear() { lock (sync) { pending.Clear(); characters = 0; } }
    }

    internal static class CommittedCapture {
        public static Sample Sample(List<FinalizedInput> commits) {
            if (commits == null || commits.Count == 0) return null;
            return new Sample { Status = "committed", Source = commits[0].Source,
                Key = commits[0].ProcessId + ":committed:" + commits[0].Window,
                Text = null, CompositionFinalized = true, Commits = commits };
        }
        public static Sample Merge(Sample first, Sample latest) {
            var commits = new List<FinalizedInput>();
            if (first.Commits != null) commits.AddRange(first.Commits);
            if (latest.Commits != null) commits.AddRange(latest.Commits);
            if (commits.Count > 0) return Sample(commits);
            if (latest.Key == first.Key) latest.CompositionFinalized |= first.CompositionFinalized;
            return latest;
        }
    }
}
