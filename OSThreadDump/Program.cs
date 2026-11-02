using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace OSThreadDump
{
    internal static class Program
    {
        private static readonly ManualResetEvent StopRequested = new ManualResetEvent(false);

        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (OptionsException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("Run OSThreadDump -? for help.");
                return 1;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }

            try
            {
                Directory.CreateDirectory(options.OutputDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Cannot use output directory '{0}': {1}", options.OutputDirectory, ex.Message);
                return 1;
            }

            // Ctrl+C finishes the dump in progress, then stops.
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                StopRequested.Set();
            };

            var dumper = new ThreadDumper(options);
            int dumps = 0;

            while (true)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                string path = Path.Combine(
                    options.OutputDirectory,
                    "threads_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".log");

                int found;
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    found = dumper.WriteDump(writer);
                }
                stopwatch.Stop();
                dumps++;

                Console.WriteLine("{0} process(es) written to {1} in {2} ms", found, Path.GetFullPath(path), stopwatch.ElapsedMilliseconds);

                if (options.IntervalSeconds == 0 || (options.Count > 0 && dumps >= options.Count))
                    break;

                // Keep to the requested interval, but always pause at least twice as long as the
                // dump took, so a slow dump cannot keep the target processes under constant inspection.
                long waitMs = Math.Max(options.IntervalSeconds * 1000L - stopwatch.ElapsedMilliseconds, 2 * stopwatch.ElapsedMilliseconds);
                if (StopRequested.WaitOne(TimeSpan.FromMilliseconds(waitMs)))
                    break;
            }

            return 0;
        }
    }
}
