using System;
using System.Linq;
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

        private static Mutex _mutex;
        private static EventWaitHandle _activateEvent;

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Single instance: a second launch signals the first (which pops
            // its window up) instead of creating a second session.
            _mutex = new Mutex(true, "LatticeVeil.Messages.Instance", out var created);
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LatticeVeil.Messages.Activate");
            if (!created)
            {
                _activateEvent.Set();
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

            ShowNewWindow();

            // Second-launch watcher: bring the existing window to the front.
            var listener = new Thread(() =>
            {
                while (_activateEvent.WaitOne())
                {
                    Current?.Dispatcher.BeginInvoke(() =>
                    {
                        var win = Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
                        if (win == null) { ShowNewWindow(); return; }
                        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                        win.Topmost = true;      // bring to front
                        win.Topmost = win.IsPinned;
                        win.Activate();
                    });
                }
            }) { IsBackground = true };
            listener.Start();

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show("Unexpected error: " + args.Exception.Message, "LatticeVeil Messages",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }

        public static void ShowNewWindow()
        {
            var win = new MainWindow();
            win.Show();
        }

        public static void RaiseSessionChanged() => SessionChanged?.Invoke();

        protected override void OnExit(ExitEventArgs e)
        {
            Service?.Dispose();
            try { if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); } _activateEvent?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
