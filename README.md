# OSThreadDump

Dumps the managed (.NET) call stack of every thread in selected processes, such as the
OutSystems platform services and IIS worker processes (`w3wp.exe`). Each thread is tagged
with its **OS thread ID** and **CPU time**, so a hot thread seen in Process Explorer,
perfmon or Task Manager can be matched to the code it is running.

Built on .NET 8 and [ClrMD](https://github.com/microsoft/clrmd). Reads both .NET Framework and
.NET (Core) processes. No debugger or dump file needed.

## Usage

```
OSThreadDump [set] [-Process name]... [options]
```

| Option | Meaning |
|---|---|
| `all` \| `iis` \| `services` \| `devtools` | Predefined process set (default `all`). |
| `-Process <name>` | Process to dump, e.g. `w3wp` or `MyService.exe`. Repeatable; combined with a set if both are given. |
| `-Interval <s>` | Keep dumping every *s* seconds until Ctrl+C. Each dump also shows the CPU each thread used since the previous one. |
| `-Count <n>` | Stop after *n* dumps (with `-Interval`). |
| `-Out <dir>` | Where to write `threads_*.log` (default: current directory). |
| `-MaxFrames <n>` | Frames printed per thread (default 200). |
| `-Passive` | Read the live process without stopping it (default). |
| `-Snapshot` | Snapshot the process first: consistent stacks, very short pause. **Recommended in production.** Windows 8.1 / Server 2012 R2+. |
| `-Suspend` | Suspend the process while reading. Freezes it for the whole dump. (`-NonInvasive` / `-Invasive` are old aliases.) |

Examples:

```
OSThreadDump iis -Snapshot
OSThreadDump services -Interval 10 -Count 30 -Out C:\Temp\dumps
OSThreadDump -Process MyWorker.exe -Process w3wp
```

Run it elevated, or as the same user as the target process.

### Finding what is burning CPU

Run with `-Interval`. From the second dump on, threads are sorted by CPU used since the
previous dump, and each process starts with a summary:

```
=== Process 4120 w3wp C:\Windows\System32\inetsrv\w3wp.exe
CLR Desktop 4.8.4645.0
AppDomain 2 /LM/W3SVC/1/ROOT-1-133...

Top 10 of 63 OS threads by CPU since previous dump:
	OS=0x1A2C (6700) Managed=41  CPU=00:03:12.410 (+9.812s, 98.1% of a core)  State=Running
	OS=0x0F10 (3856) native  CPU=00:00:04.002 (+0.031s, 0.3% of a core)  State=Wait:UserRequest
	...

Thread OS=0x1A2C (6700) Managed=41  CPU=00:03:12.410 (+9.812s, 98.1% of a core)  State=Running
	000000e4f1b7e1a0 00007ffa1c2d3e44 MyApp.Pricing.Recalculate(...)
	...
```

## 32-bit processes

A 64-bit process can only read the stacks of other 64-bit processes. Use
`OSThreadDump.x86.exe` for 32-bit targets (for example the `devtools` set). A process with
the wrong bitness is listed as `[SKIPPED]` along with its CPU summary.

## Download

Each GitHub Actions run (**Actions** tab → latest run → **Artifacts**) produces two
stand-alone executables. Copy one to the server and run it; nothing needs to be installed:

- `OSThreadDump.exe` for 64-bit processes (IIS `w3wp`, the platform services)
- `OSThreadDump.x86.exe` for 32-bit processes

## Building

Requires the .NET 8 SDK (Windows, Linux or WSL). The tool itself only runs on Windows.

**Self-contained single exe (recommended)**, about 30–35 MB, nothing to install on the server:

```
dotnet publish OSThreadDump/OSThreadDump.csproj -c Release -r win-x64 -o dist
dotnet publish OSThreadDump/OSThreadDump.csproj -c Release -r win-x86 -o dist
```

Result: `dist/OSThreadDump.exe` and `dist/OSThreadDump.x86.exe`.

**Small single exe**, under 1 MB. It uses the .NET 8 runtime already on the server
(OutSystems Platform Server 11.27 and later installs the .NET 8 Hosting Bundle, which includes it):

```
dotnet publish OSThreadDump/OSThreadDump.csproj -c Release -r win-x64 -o dist-small -p:SelfContained=false
```

On WSL/Linux, if `dotnet` aborts with "Couldn't find a valid ICU package", install it with
`sudo apt install libicu-dev`, or run `export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` first.
