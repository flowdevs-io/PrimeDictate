using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// Ties helper processes to this app on Windows: they go into a job object that kills them when the app's last handle
/// to it closes, which includes a crash or a force-kill (a rebuild, Task Manager). Without it a killed app leaves the
/// GPU-holding <c>nemo-speech</c> worker running. Every platform also kills the helpers when the app exits normally or
/// after an unhandled error; only a force-kill on Linux or macOS can still leave one behind.
/// </summary>
public static class ChildProcessJob
{
    private static readonly object Gate = new();
    private static IntPtr job;
    private static readonly List<Process> Started = [];

    static ChildProcessJob() => AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();

    /// <summary>Kills every helper still running. Runs when the app exits on any platform, including after an unhandled error.</summary>
    public static void KillAll()
    {
        Process[] running;
        lock (Gate)
        {
            running = [.. Started];
            Started.Clear();
        }

        foreach (var process in running)
        {
            try
            {
                if (!IsGone(process))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>True when the process ended or its object was already disposed.</summary>
    private static bool IsGone(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <returns>True when the process is now tied to this app by the operating system (Windows job object).</returns>
    public static bool TryAdd(Process process)
    {
        lock (Gate)
        {
            Started.RemoveAll(IsGone);
            Started.Add(process);
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            lock (Gate)
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateKillOnCloseJob();
                }

                return job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return handle;
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
