using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LatticeVeil.Messages
{
    public partial class App : Application
    {
        public static string LauncherToken { get; private set; } = "";
        public static string Username { get; private set; } = "";
        public static string UserId { get; private set; } = "";
        public static MessagingService Service { get; private set; }

        public static event Action SessionChanged;

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Dup-protection: a second launch focuses the existing instance
            // instead of creating a second session.
            bool created;
            _mutex = new Mutex(true, "LatticeVeil.Messages.Instance", out created);
            if (!created)
            {
                MessageBox.Show("LatticeVeil Messages is already running. Use the launcher's MESSAGES button to reopen it.",
                    "LatticeVeil Messages", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Secure session handoff: the launcher spawns this process with the
            // launcher's DPAPI-protected session token in the environment.
            LauncherToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? "").Trim();
            var envUser = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? "").Trim();
            if (!string.IsNullOrEmpty(envUser)) Username = envUser;

            // Fallback: read the same DPAPI-protected file the launcher uses
            // (same Windows user, same machine — the launcher's own mechanism).
            if (string.IsNullOrEmpty(LauncherToken))
            {
                try
                {
                    var record = AuthFile.Read();
                    if (record != null)
                    {
                        LauncherToken = record.Token ?? "";
                        if (string.IsNullOrEmpty(Username)) Username = record.Username ?? "";
                        UserId = record.UserId ?? "";
                    }
                }
                catch { /* fall through to unauthenticated UI */ }
            }

            Service = new MessagingService(LauncherToken);
            Service.Start();

            // The realtime NOTICES are ids-only; prefetch resolves sender
            // names via bootstrap profiles as soon as they arrive.
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("Unexpected error: " + args.Exception.Message, "LatticeVeil Messages",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        private Mutex _mutex;

        public static void RaiseSessionChanged() => SessionChanged?.Invoke();

        protected override void OnExit(ExitEventArgs e)
        {
            Service?.Dispose();
            try
            {
                if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); }
            }
            catch { }
            base.OnExit(e);
        }
    }
}
