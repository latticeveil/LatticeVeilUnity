using System;
using System.Linq;
using System.Windows.Forms;

namespace LatticeVeil.Installer
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // CLI modes give the installer testable, scriptable behavior (and let the
            // future setup bootstrap headlessly). No args = GUI wizard.
            // Optional leading --root <path> overrides the install root for this run.
            var serviceArgs = args;
            if (args.Length >= 2 && args[0] == "--root")
            {
                serviceArgs = args.Skip(2).ToArray();
                if (serviceArgs.Length == 0)
                {
                    new InstallService(args[1]).Apply();
                    Console.WriteLine("LATTICEVEIL_INSTALL_ROOT set to: " + args[1]);
                    return 0;
                }
            }

            var service = new InstallService(
                args.Length >= 2 && args[0] == "--root" ? args[1] : null);

            if (serviceArgs.Length > 0)
                return RunCli(service, serviceArgs);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(service));
            return 0;
        }

        static int RunCli(InstallService service, string[] args)
        {
            switch (args[0])
            {
                case "--set-root":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("usage: --set-root <path>"); return 2; }
                    service.Apply();
                    Console.WriteLine($"LATTICEVEIL_INSTALL_ROOT set to: {service.RootDir}");
                    return 0;
                }
                case "--clear-root":
                {
                    service.ClearPersistedRoot();
                    Console.WriteLine("LATTICEVEIL_INSTALL_ROOT cleared (portable mode).");
                    return 0;
                }
                case "--list-versions":
                {
                    var versions = service.GetInstalledVersions();
                    Console.WriteLine($"Install root: {service.RootDir}");
                    Console.WriteLine($"Installed versions: {versions.Count}");
                    foreach (var v in versions)
                        Console.WriteLine($"  {v.Tag}  {v.SizeBytes / 1_000_000.0:0} MB  exe={v.ExePath}");
                    return 0;
                }
                case "--remove-version":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("usage: --remove-version <tag>"); return 2; }
                    var ok = service.UninstallVersion(args[1]);
                    Console.WriteLine(ok ? $"Removed: {args[1]}" : $"Not installed: {args[1]}");
                    return ok ? 0 : 1;
                }
                case "--install-launcher":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("usage: --install-launcher <build-folder>"); return 2; }
                    var dest = service.InstallLauncher(args[1]);
                    Console.WriteLine($"Launcher installed to: {dest}");
                    return 0;
                }
                case "--help":
                default:
                    Console.WriteLine("LatticeVeil Installer");
                    Console.WriteLine("  (no args)                open the GUI wizard");
                    Console.WriteLine("  --set-root <path>        create structure + persist LATTICEVEIL_INSTALL_ROOT");
                    Console.WriteLine("  --clear-root             remove the persisted variable");
                    Console.WriteLine("  --list-versions          list installed game versions");
                    Console.WriteLine("  --remove-version <tag>   uninstall one game version");
                    Console.WriteLine("  --install-launcher <dir> copy a launcher build into <root>\\Launcher");
                    return args.Length > 0 && args[0] == "--help" ? 0 : 2;
            }
        }
    }
}
