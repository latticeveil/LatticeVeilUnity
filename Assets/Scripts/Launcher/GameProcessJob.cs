using System;
using System.Runtime.InteropServices;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Owns the Windows job objects that bind the launcher and the game into a
    /// single process collection:
    ///
    ///   LatticeVeil  (outer "group" job — launcher + every game version)
    ///    ├─ LatticeVeilMonoGame.exe / LatticeVeil.exe   (the game)
    ///    └─ the launcher itself
    ///
    ///   └─ inner "sandbox" job (game only — UI restrictions)
    ///
    /// The launcher assigns ITSELF to the outer job and the game is started as
    /// its child (children inherit the outer job automatically) and then also
    /// assigned into the nested sandbox job. Because both processes live in the
    /// same job tree, Task Manager shows ONE collection — "LatticeVeil" (the
    /// launcher window title) — with the game and the launcher as two
    /// sub-processes, no matter which game version is running.
    ///
    /// Also gives:
    ///   • kill-on-close: the game can never outlive the launcher (no orphans).
    ///   • sandbox-lite for the game: UI restrictions stop it from reading the
    ///     clipboard, touching inherited handles, registering global atoms, or
    ///     shutting down/restarting the system.
    ///
    /// NOTE: this is process grouping + lifetime tying, NOT a true security
    /// sandbox — the game still has normal file access.
    /// </summary>
    internal static class GameProcessJob
    {
        private static readonly object _lock = new object();
        private static IntPtr _groupJobHandle = IntPtr.Zero;   // launcher + game
        private static IntPtr _sandboxJobHandle = IntPtr.Zero; // game only, nested
        private static bool _launcherAssigned;

        /// <summary>
        /// Creates the job tree and puts the launcher into the group job BEFORE
        /// the game process is started, so the game inherits the group
        /// membership at creation time (one Task Manager collection from the
        /// first millisecond). Non-fatal on failure.
        /// </summary>
        public static void PrepareForLaunch()
        {
            lock (_lock)
            {
                try
                {
                    EnsureJobsLocked();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[GameProcessJob] PrepareForLaunch failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Binds a freshly started game process into the collection. Creates the
        /// jobs on first use (assigning the launcher to the group job in the
        /// process). Non-fatal on failure.
        /// </summary>
        public static void Attach(System.Diagnostics.Process process)
        {
            if (process == null)
                return;

            try
            {
                lock (_lock)
                {
                    EnsureJobsLocked();

                    // Put the game into the nested sandbox job. It is already a
                    // member of the group job by inheritance (child of the
                    // launcher); the sandbox job is nested inside the group job,
                    // so this single assignment covers both.
                    if (!AssignProcessToJobObject(_sandboxJobHandle, process.Handle))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: the game still runs normally, just without the
                // Task Manager grouping / lifetime tie.
                UnityEngine.Debug.LogWarning($"[GameProcessJob] Attach failed: {ex.Message}");
            }
        }

        private static void EnsureJobsLocked()
        {
            if (_groupJobHandle == IntPtr.Zero)
            {
                _groupJobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_groupJobHandle == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                SetKillOnClose(_groupJobHandle);
            }

            if (!_launcherAssigned)
            {
                // The launcher joins its own group job so both processes share
                // one collection. Best-effort: if the launcher already runs
                // inside a foreign job that refuses the assignment, grouping
                // silently degrades but everything keeps working.
                if (AssignProcessToJobObject(_groupJobHandle, GetCurrentProcess()))
                {
                    _launcherAssigned = true;
                }
                else
                {
                    UnityEngine.Debug.LogWarning(
                        $"[GameProcessJob] Could not assign launcher to group job (error {Marshal.GetLastWin32Error()}); continuing without self-grouping.");
                    _launcherAssigned = true; // don't retry every launch
                }
            }

            if (_sandboxJobHandle == IntPtr.Zero)
            {
                _sandboxJobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_sandboxJobHandle == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                SetKillOnClose(_sandboxJobHandle);
                ApplyUiRestrictions(_sandboxJobHandle);

                // Nest the sandbox job inside the group job (job hierarchy).
                // Best-effort: if nesting is refused the game is only sandboxed,
                // not grouped — attach still succeeds via process inheritance.
                if (!AssignProcessToJobObject(_groupJobHandle, _sandboxJobHandle))
                    UnityEngine.Debug.LogWarning(
                        $"[GameProcessJob] Job nesting failed (error {Marshal.GetLastWin32Error()}); continuing without nesting.");
            }
        }

        private static void SetKillOnClose(IntPtr jobHandle)
        {
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
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Applies the sandbox UI restrictions to a job. Best-effort: if the
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

        /// <summary>
        /// Instant Quit path: closes both job handles WITHOUT killing the game
        /// first. The game was started inside the launcher's group job, so it
        /// inherited that membership at creation and KEEPS running after the
        /// launcher exits (only the job handles disappear). Callers must have
        /// finished any work that depends on the game being tied to the launcher.
        /// </summary>
        public static void ReleaseForInstantQuit()
        {
            lock (_lock)
            {
                if (_sandboxJobHandle != IntPtr.Zero)
                {
                    CloseHandle(_sandboxJobHandle);
                    _sandboxJobHandle = IntPtr.Zero;
                }

                if (_groupJobHandle != IntPtr.Zero)
                {
                    CloseHandle(_groupJobHandle);
                    _groupJobHandle = IntPtr.Zero;
                }

                _launcherAssigned = false;
            }
        }

        /// <summary>
        /// Closes both job handles. Kill-on-close ends any remaining job members
        /// (the game) — the launcher itself is already exiting at this point.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_groupJobHandle != IntPtr.Zero)
                {
                    CloseHandle(_groupJobHandle);
                    _groupJobHandle = IntPtr.Zero;
                }

                if (_sandboxJobHandle != IntPtr.Zero)
                {
                    CloseHandle(_sandboxJobHandle);
                    _sandboxJobHandle = IntPtr.Zero;
                }

                _launcherAssigned = false;
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}
