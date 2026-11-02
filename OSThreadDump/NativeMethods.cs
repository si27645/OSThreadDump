using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OSThreadDump
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

        /// <summary>
        /// True if the process is 64-bit, false if 32-bit, null if it could not be determined
        /// (typically access denied).
        /// </summary>
        public static bool? Is64BitProcess(Process process)
        {
            if (!Environment.Is64BitOperatingSystem)
                return false;

            try
            {
                bool wow64;
                if (!IsWow64Process(process.Handle, out wow64))
                    return null;
                return !wow64;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
