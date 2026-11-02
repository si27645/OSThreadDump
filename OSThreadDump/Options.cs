using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OSThreadDump
{
    internal enum AttachMode
    {
        /// <summary>Read the live process without stopping it. Lowest impact, but stacks can be slightly inconsistent.</summary>
        Passive,
        /// <summary>Suspend the process while its stacks are read.</summary>
        Suspend,
        /// <summary>Take a process snapshot (PssCaptureSnapshot) and read that. Consistent stacks, very short pause.</summary>
        Snapshot,
    }

    internal sealed class OptionsException : Exception
    {
        public OptionsException(string message) : base(message) { }
    }

    internal sealed class Options
    {
        public static readonly IReadOnlyDictionary<string, string[]> ProcessSets =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "all", new[] { "LogServer", "DeployService", "CompilerService", "Scheduler", "SMSConnector", "w3wp" } },
                { "iis", new[] { "w3wp" } },
                { "services", new[] { "LogServer", "DeployService", "CompilerService", "Scheduler", "SMSConnector" } },
                // 32-bit processes: run OSThreadDump.x86.exe for these.
                { "devtools", new[] { "ServiceStudio", "IntegrationStudio" } },
            };

        public const int DefaultMaxFrames = 200;

        public string[] ProcessNames { get; private set; }
        public AttachMode Mode { get; private set; } = AttachMode.Passive;
        public int IntervalSeconds { get; private set; }
        public int Count { get; private set; }
        public int MaxFrames { get; private set; } = DefaultMaxFrames;
        public string OutputDirectory { get; private set; } = ".";
        public bool ShowHelp { get; private set; }

        public static string Usage
        {
            get
            {
                return
@"OSThreadDump - dumps the managed stacks of every thread of selected .NET processes,
tagged with OS thread IDs and CPU time so you can match them to Process Explorer / perfmon.

Usage:
  OSThreadDump [set] [-Process name]... [options]

Process selection (default: all):
  all | iis | services | devtools   Predefined process set (" + string.Join(", ", ProcessSets.Keys) + @")
  -Process <name>                   Process to dump, e.g. w3wp or MyService.exe. Repeatable.
                                    Combined with a set if both are given.

Options:
  -Interval <seconds>   Keep dumping every N seconds until Ctrl+C (or -Count is reached).
                        Each dump then also shows the CPU each thread used since the previous one.
  -Count <n>            Stop after n dumps (requires -Interval).
  -Out <directory>      Where to write threads_*.log files (default: current directory).
  -MaxFrames <n>        Maximum frames printed per thread (default: " + DefaultMaxFrames + @").
  -Passive              Read the live process without stopping it (default).
  -Snapshot             Take a process snapshot first: consistent stacks, very short pause.
                        Recommended for production. Needs Windows 8.1 / Server 2012 R2 or later.
  -Suspend              Suspend the process while reading its stacks. The process is frozen
                        for the whole dump; avoid on busy production servers.
  -NonInvasive, -Invasive   Old names, both now mean -Suspend.
  -h, -?                Show this help.

Bitness: a 64-bit OSThreadDump.exe can only read 64-bit processes. Use OSThreadDump.x86.exe
for 32-bit processes (such as the devtools set). Mismatched processes are listed as skipped.

Examples:
  OSThreadDump iis -Snapshot
  OSThreadDump services -Interval 10 -Count 30 -Out C:\Temp\dumps
  OSThreadDump -Process MyWorker.exe -Process w3wp";
            }
        }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            var names = new List<string>();
            string set = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "-h":
                    case "-?":
                    case "/?":
                    case "-help":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "-passive":
                        options.Mode = AttachMode.Passive;
                        break;
                    case "-snapshot":
                        options.Mode = AttachMode.Snapshot;
                        break;
                    case "-suspend":
                    case "-noninvasive":
                    case "-invasive":
                        options.Mode = AttachMode.Suspend;
                        break;
                    case "-interval":
                        options.IntervalSeconds = PositiveInt(args, ref i);
                        break;
                    case "-count":
                        options.Count = PositiveInt(args, ref i);
                        break;
                    case "-maxframes":
                        options.MaxFrames = PositiveInt(args, ref i);
                        break;
                    case "-out":
                        options.OutputDirectory = Value(args, ref i);
                        break;
                    case "-process":
                        names.Add(NormalizeProcessName(Value(args, ref i)));
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("/", StringComparison.Ordinal))
                            throw new OptionsException("Unknown option: " + arg);
                        if (set != null)
                            throw new OptionsException("Only one process set can be given (got '" + set + "' and '" + arg + "').");
                        if (!ProcessSets.ContainsKey(arg))
                            throw new OptionsException("Unknown process set '" + arg + "'. Valid sets: " + string.Join(", ", ProcessSets.Keys) + ".");
                        set = arg;
                        break;
                }
            }

            if (options.Count > 0 && options.IntervalSeconds == 0)
                throw new OptionsException("-Count only makes sense together with -Interval.");

            if (set != null || names.Count == 0)
                names.AddRange(ProcessSets[set ?? "all"]);

            options.ProcessNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return options;
        }

        private static string Value(string[] args, ref int i)
        {
            string option = args[i];
            if (i + 1 >= args.Length)
                throw new OptionsException(option + " needs a value.");
            i++;
            return args[i];
        }

        private static int PositiveInt(string[] args, ref int i)
        {
            string option = args[i];
            string value = Value(args, ref i);
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result <= 0)
                throw new OptionsException(option + " needs a whole number greater than 0 (got '" + value + "').");
            return result;
        }

        // Process.GetProcessesByName wants the name without the extension.
        private static string NormalizeProcessName(string name)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }
    }
}
