using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace OSThreadDump
{
    /// <summary>
    /// Writes one dump (all selected processes) per call to <see cref="WriteDump"/>. Keeps the
    /// per-thread CPU time of the previous dump so repeated dumps can show CPU used in between.
    /// </summary>
    internal sealed class ThreadDumper
    {
        private const int TopThreadsShown = 10;

        private readonly Options _options;
        private Dictionary<string, TimeSpan> _previousCpu = new Dictionary<string, TimeSpan>();
        private DateTime? _previousDumpTime;

        public ThreadDumper(Options options)
        {
            _options = options;
        }

        /// <returns>The number of processes found.</returns>
        public int WriteDump(TextWriter writer)
        {
            DateTime now = DateTime.Now;
            double? secondsSincePrevious = _previousDumpTime.HasValue
                ? (now - _previousDumpTime.Value).TotalSeconds
                : (double?)null;

            writer.WriteLine("OSThreadDump {0}  host={1}  {2}-bit  mode={3}",
                now.ToString("o", CultureInfo.InvariantCulture),
                Environment.MachineName,
                Environment.Is64BitProcess ? 64 : 32,
                _options.Mode);
            if (secondsSincePrevious.HasValue)
                writer.WriteLine("CPU deltas cover the last {0:0.0} s", secondsSincePrevious.Value);

            var currentCpu = new Dictionary<string, TimeSpan>();
            int found = 0;

            foreach (string name in _options.ProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch (Exception ex)
                {
                    writer.WriteLine();
                    writer.WriteLine("[ERROR] Could not list processes named {0}: {1}", name, Describe(ex));
                    continue;
                }

                foreach (Process process in processes)
                {
                    using (process)
                    {
                        found++;
                        writer.WriteLine();
                        DumpProcess(process, writer, currentCpu, secondsSincePrevious);
                        writer.Flush();
                    }
                }
            }

            if (found == 0)
            {
                writer.WriteLine();
                writer.WriteLine("No matching processes are running ({0}).", string.Join(", ", _options.ProcessNames));
            }

            _previousCpu = currentCpu;
            _previousDumpTime = now;
            return found;
        }

        private void DumpProcess(Process process, TextWriter writer, Dictionary<string, TimeSpan> currentCpu, double? secondsSincePrevious)
        {
            writer.WriteLine("=== Process {0} {1} {2}", process.Id, process.ProcessName, GetFileName(process));

            Dictionary<int, OsThread> osThreads = ReadOsThreads(process, currentCpu, secondsSincePrevious);

            bool? targetIs64 = NativeMethods.Is64BitProcess(process);
            if (targetIs64.HasValue && targetIs64.Value != Environment.Is64BitProcess)
            {
                writer.WriteLine("[SKIPPED] Process is {0}-bit; this OSThreadDump is {1}-bit. Use {2} to read its managed stacks.",
                    targetIs64.Value ? 64 : 32,
                    Environment.Is64BitProcess ? 64 : 32,
                    targetIs64.Value ? "the 64-bit OSThreadDump.exe" : "OSThreadDump.x86.exe");
                WriteTopThreads(writer, osThreads, new Dictionary<uint, ClrThread>());
                return;
            }

            var managedByOsId = new Dictionary<uint, ClrThread>();
            try
            {
                using (DataTarget target = Attach(process.Id))
                {
                    if (target.ClrVersions.Length == 0)
                    {
                        writer.WriteLine("[INFO] No .NET runtime is loaded in this process.");
                        WriteTopThreads(writer, osThreads, managedByOsId);
                        return;
                    }

                    foreach (ClrInfo clr in target.ClrVersions)
                    {
                        using (ClrRuntime runtime = clr.CreateRuntime())
                        {
                            writer.WriteLine("CLR {0} {1}", clr.Flavor, clr.Version);
                            foreach (ClrAppDomain domain in runtime.AppDomains)
                                writer.WriteLine("AppDomain {0} {1}", domain.Id, domain.Name);

                            foreach (ClrThread thread in runtime.Threads)
                            {
                                if (thread.IsAlive && thread.OSThreadId != 0)
                                    managedByOsId[thread.OSThreadId] = thread;
                            }

                            writer.WriteLine();
                            WriteTopThreads(writer, osThreads, managedByOsId);
                            WriteManagedStacks(writer, osThreads, managedByOsId.Values);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Typical causes: access denied (run elevated / as the app pool identity),
                // process exited, attach timeout, or Snapshot mode on an old Windows version.
                writer.WriteLine("[ERROR] Could not read managed stacks: {0}", Describe(ex));
                if (managedByOsId.Count == 0)
                    WriteTopThreads(writer, osThreads, managedByOsId);
            }
        }

        private DataTarget Attach(int pid)
        {
            switch (_options.Mode)
            {
                case AttachMode.Snapshot:
                    return DataTarget.CreateSnapshotAndAttach(pid);
                case AttachMode.Suspend:
                    return DataTarget.AttachToProcess(pid, suspend: true);
                default:
                    return DataTarget.AttachToProcess(pid, suspend: false);
            }
        }

        private void WriteManagedStacks(TextWriter writer, Dictionary<int, OsThread> osThreads, IEnumerable<ClrThread> threads)
        {
            // Busiest threads first: by CPU since the previous dump, else by total CPU.
            var ordered = threads
                .Select(t => new { Thread = t, Os = Lookup(osThreads, t.OSThreadId) })
                .OrderByDescending(x => x.Os != null && x.Os.CpuDelta.HasValue ? x.Os.CpuDelta.Value : TimeSpan.Zero)
                .ThenByDescending(x => x.Os != null ? x.Os.Cpu : TimeSpan.Zero);

            foreach (var item in ordered)
            {
                // Enumerate once; take one extra frame to know whether we truncated.
                List<ClrStackFrame> frames;
                try
                {
                    frames = item.Thread.EnumerateStackTrace().Take(_options.MaxFrames + 1).ToList();
                }
                catch (Exception ex)
                {
                    writer.WriteLine("Thread OS=0x{0:X} Managed={1}: [ERROR] {2}", item.Thread.OSThreadId, item.Thread.ManagedThreadId, Describe(ex));
                    writer.WriteLine();
                    continue;
                }

                if (frames.Count == 0)
                    continue;

                writer.WriteLine("Thread OS=0x{0:X} ({0}) Managed={1}{2}",
                    item.Thread.OSThreadId,
                    item.Thread.ManagedThreadId,
                    item.Os != null ? "  " + item.Os.Describe() : "");

                foreach (ClrStackFrame frame in frames.Take(_options.MaxFrames))
                    writer.WriteLine("\t{0:x16} {1:x16} {2}", frame.StackPointer, frame.InstructionPointer, frame);

                if (frames.Count > _options.MaxFrames)
                    writer.WriteLine("\t[TRUNCATED after {0} frames; use -MaxFrames to show more]", _options.MaxFrames);

                writer.WriteLine();
            }
        }

        private static void WriteTopThreads(TextWriter writer, Dictionary<int, OsThread> osThreads, Dictionary<uint, ClrThread> managedByOsId)
        {
            if (osThreads.Count == 0)
                return;

            bool haveDeltas = osThreads.Values.Any(t => t.CpuDelta.HasValue);
            var top = osThreads.Values
                .OrderByDescending(t => haveDeltas ? (t.CpuDelta ?? TimeSpan.Zero) : t.Cpu)
                .Take(TopThreadsShown)
                .ToList();

            writer.WriteLine("Top {0} of {1} OS threads by {2}:", top.Count, osThreads.Count, haveDeltas ? "CPU since previous dump" : "total CPU");
            foreach (OsThread t in top)
            {
                ClrThread managed;
                string kind = managedByOsId.TryGetValue((uint)t.Id, out managed)
                    ? "Managed=" + managed.ManagedThreadId
                    : "native";
                writer.WriteLine("\tOS=0x{0:X} ({0}) {1}  {2}", t.Id, kind, t.Describe());
            }
            writer.WriteLine();
        }

        private Dictionary<int, OsThread> ReadOsThreads(Process process, Dictionary<string, TimeSpan> currentCpu, double? secondsSincePrevious)
        {
            var result = new Dictionary<int, OsThread>();
            ProcessThreadCollection threads;
            try
            {
                threads = process.Threads;
            }
            catch (Exception)
            {
                return result;
            }

            foreach (ProcessThread thread in threads)
            {
                try
                {
                    var info = new OsThread
                    {
                        Id = thread.Id,
                        Cpu = thread.TotalProcessorTime,
                        State = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                            ? "Wait:" + thread.WaitReason
                            : thread.ThreadState.ToString(),
                    };

                    // OS thread IDs get reused, so include the start time in the key.
                    string key = process.Id + ":" + thread.Id + ":" + StartTicks(thread);
                    currentCpu[key] = info.Cpu;

                    TimeSpan previous;
                    if (_previousCpu.TryGetValue(key, out previous))
                    {
                        info.CpuDelta = info.Cpu - previous;
                        if (secondsSincePrevious.HasValue && secondsSincePrevious.Value > 0)
                            info.CpuPercent = 100.0 * info.CpuDelta.Value.TotalSeconds / secondsSincePrevious.Value;
                    }

                    result[info.Id] = info;
                }
                catch (Exception)
                {
                    // The thread exited while we were reading it.
                }
            }
            return result;
        }

        private static long StartTicks(ProcessThread thread)
        {
            try
            {
                return thread.StartTime.Ticks;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static OsThread Lookup(Dictionary<int, OsThread> osThreads, uint osThreadId)
        {
            OsThread result;
            return osThreads.TryGetValue((int)osThreadId, out result) ? result : null;
        }

        private static string GetFileName(Process process)
        {
            try
            {
                return process.MainModule.FileName;
            }
            catch (Exception)
            {
                // Access denied, or a process of the other bitness.
                return "";
            }
        }

        private static string Describe(Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }

        private sealed class OsThread
        {
            public int Id;
            public TimeSpan Cpu;
            public TimeSpan? CpuDelta;
            public double? CpuPercent;
            public string State;

            public string Describe()
            {
                string text = "CPU=" + Cpu.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                if (CpuDelta.HasValue)
                {
                    text += string.Format(CultureInfo.InvariantCulture, " (+{0:0.000}s", CpuDelta.Value.TotalSeconds);
                    if (CpuPercent.HasValue)
                        text += string.Format(CultureInfo.InvariantCulture, ", {0:0.0}% of a core", CpuPercent.Value);
                    text += ")";
                }
                return text + "  State=" + State;
            }
        }
    }
}
