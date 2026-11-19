using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeGraph.Core;

/// <summary>
/// On Windows the install folder is a junction to the folder of one release, in <c>.&lt;name&gt;.versions</c> beside it
/// (issue #9): an update points the junction at a new release folder while programs still run. .NET loads assemblies
/// and native libraries when they are first used, through the path the process started from. A process started
/// through the junction thus loads files of the new release after an update, beside the files of its own release
/// (measured: a 1.2.0 server held files in the 1.5.0 folder after six tool calls). So the executables start themselves
/// again from the release folder that the junction points at, and only wait for that child.
/// </summary>
public static class ReleaseFolder
{
    /// <summary>The install folder (the junction) that this process was started through; set for the child.</summary>
    public const string LaunchedFromVariable = "CODEGRAPH_LAUNCHED_FROM";

    /// <summary>
    /// The release folder to run from, when this process started through the junction of the install layout; else
    /// null. Only a junction whose target is in the <c>.&lt;name&gt;.versions</c> folder beside it counts, so a link
    /// that a person made, a subst drive or a network drive keeps its old behaviour.
    /// </summary>
    public static string? RealFolder(string baseDirectory)
    {
        var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (dir.Parent is not { } parent || dir.LinkTarget is not { } target)
        {
            return null;
        }

        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, parent.FullName));
        var versions = Path.Combine(parent.FullName, $".{dir.Name}.versions") + Path.DirectorySeparatorChar;
        return real.StartsWith(versions, StringComparison.OrdinalIgnoreCase) && Directory.Exists(real) ? real : null;
    }

    /// <summary>
    /// If this process started through the junction, runs the same executable from the release folder with the same
    /// arguments, standard streams and working folder, and returns its exit code. Returns null when there is nothing to
    /// do: not Windows, not started through the junction, or already the child.
    /// </summary>
    public static int? RunFromRealFolder(string[] args)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } processPath
            || RealFolder(AppContext.BaseDirectory) is not { } real)
        {
            return null;
        }

        var exe = Path.Combine(real, Path.GetFileName(processPath));
        if (!File.Exists(exe))
        {
            return null;
        }

        // No redirection: the child gets this process's standard handles, so an MCP client's pipes reach it as they are.
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment[LaunchedFromVariable] = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        // Ctrl+C reaches both processes in a console; the child stops cleanly, and this one waits for it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var job = new KillOnCloseJob();
        using var child = Process.Start(info) ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            job.Assign(child);
        }
        catch
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
            }
            throw;
        }
        child.WaitForExit();
        return child.ExitCode;
    }

    private sealed class KillOnCloseJob : IDisposable
    {
        private const uint KillOnClose = 0x00002000;
        private readonly SafeFileHandle handle;

        public KillOnCloseJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnClose },
            };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }

    /// <summary>
    /// The folder to name in a config that must outlive this release: the install folder (the junction) when this
    /// process runs from a release folder of the junction layout, else this executable's own folder.
    /// </summary>
    public static string StableBaseDirectory()
    {
        var launchedFrom = Environment.GetEnvironmentVariable(LaunchedFromVariable);
        return !string.IsNullOrEmpty(launchedFrom) && Directory.Exists(launchedFrom) && RealFolder(launchedFrom) is not null
            ? launchedFrom + Path.DirectorySeparatorChar
            : AppContext.BaseDirectory;
    }
}
