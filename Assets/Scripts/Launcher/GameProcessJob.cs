using System;
using System.Runtime.InteropServices;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Owns a Windows job object that ties the running game process to the
    /// launcher. The game is assigned into the launcher-owned job, so:
    ///   • Task Manager shows it nested under LatticeLauncher.exe
    ///     (LatticeLauncher.exe > LatticeVeilMonoGame.exe / LatticeVeil.exe)
    ///   • kill-on-close means the game can never outlive the launcher
    ///     (no orphaned game processes when the launcher is closed).
    ///   • the game is sandboxed: UI restrictions stop it from reading the
    ///     clipboard, touching inherited handles, registering global atoms,
    ///     or shutting down/restarting the system.
    /// </summary>
    internal static class GameProcessJob
    {
        private static readonly object _lock = new object();
        private static IntPtr _jobHandle = IntPtr.Zero;

        /// <summary>Assigns the game process into the launcher's job object. Non-fatal on failure.</summary>
        public static void Attach(System.Diagnostics.Process process)
        {
            if (process == null)
                return;

            try
            {
                lock (_lock)
                {
                    if (_jobHandle == IntPtr.Zero)
                    {
                        _jobHandle = CreateJobObject(IntPtr.Zero, null);
                        if (_jobHandle == IntPtr.Zero)
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                        {
                            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                            {
                                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                            }
                        };

                        var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                        var ptr = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(info, ptr, false);
                            if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(ptr);
                        }

                        // Sandbox UI restrictions for every process in the job.
                        ApplyUiRestrictions(_jobHandle);
                    }

                    if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: the game still runs normally, just without the
                // launcher nesting / lifetime tie.
                UnityEngine.Debug.LogWarning($"[GameProcessJob] Attach failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the sandbox UI restrictions to the job. Best-effort: if the
        /// system refuses them the game simply runs without that restriction.
        /// </summary>
        private static void ApplyUiRestrictions(IntPtr jobHandle)
        {
            var restrictions = (uint)(
                JOB_OBJECT_UILIMIT_HANDLES |
                JOB_OBJECT_UILIMIT_READCLIPBOARD |
                JOB_OBJECT_UILIMIT_WRITECLIPBOARD |
                JOB_OBJECT_UILIMIT_GLOBALATOMS |
                JOB_OBJECT_UILIMIT_EXITWINDOWS);

            var ptr = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(ptr, unchecked((int)restrictions));
                SetInformationJobObject(jobHandle, JobObjectBasicUIRestrictions, ptr, sizeof(uint));
            }
            catch
            {
                // Non-fatal: sandbox-lite is best effort.
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>Closes the job handle (kills any remaining job members via kill-on-close).</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_jobHandle != IntPtr.Zero)
                {
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;
        private const int JobObjectBasicUIRestrictions = 4;

        private const uint JOB_OBJECT_UILIMIT_HANDLES = 0x00000001;
        private const uint JOB_OBJECT_UILIMIT_READCLIPBOARD = 0x00000002;
        private const uint JOB_OBJECT_UILIMIT_WRITECLIPBOARD = 0x00000004;
        private const uint JOB_OBJECT_UILIMIT_GLOBALATOMS = 0x00000020;
        private const uint JOB_OBJECT_UILIMIT_EXITWINDOWS = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
