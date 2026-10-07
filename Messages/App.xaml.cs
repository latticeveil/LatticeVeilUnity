using System;
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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Secure session handoff: the launcher spawns this process with the
            // DPAPI-protected session's launcher token in the environment.
            LauncherToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? "").Trim();

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
                        Username = record.Username ?? "";
                        UserId = record.UserId ?? "";
                    }
                }
                catch { /* fall through to unauthenticated UI */ }
            }

            Service = new MessagingService(LauncherToken);
            Service.Start();

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("Unexpected error: " + args.Exception.Message, "LatticeVeil Messages",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        public static void RaiseSessionChanged() => SessionChanged?.Invoke();

        protected override void OnExit(ExitEventArgs e)
        {
            Service?.Dispose();
            base.OnExit(e);
        }
    }
}
