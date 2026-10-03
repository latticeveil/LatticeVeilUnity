using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace LatticeVeilUninstaller
{
    public partial class MainWindow : Window
    {
        private const string InstallMetadataFile = "latticeveil_install_info.txt";
        private const string GameExe = "LatticeVeilMonoGame.exe";
        private const string InstallRootEnvVar = "LATTICEVEIL_INSTALL_ROOT";
        private const string VersionsFolderName = "Versions";
        private const string LauncherFolderName = "Launcher";
        private const string CurrentMetadataLayout = "2";
        private readonly List<WorldInfo> _worlds = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private string _installPath = "";
        private string _documentsPath = "";
        private string _installedVersion = "";
        private string _backupRootPath = "";
        private bool _isInitialized = false;
        private bool _uninstallCompleted = false;
        private bool _cleanupScheduled = false;
        private bool _hasWorldsAvailable = false;
        private bool _isNewLayout = false;
        private bool _backupLocationSelected = false;
        private int _currentPage = 0;
        private readonly Random _random = new Random();

        private readonly List<string> _splashMessages = new List<string>
        {
            "Sadly saying goodbye to LatticeVeil...",
            "Removing your adventures...",
            "Cleaning up the memories...",
            "Farewell, brave explorer...",
            "Packing away the moonlight...",
            "Sweeping away the stardust...",
            "Closing the chapter on LatticeVeil...",
            "Vanishing into the digital sunset...",
            "Erasing traces of your journey...",
            "Bidding farewell to the void..."
        };

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            _cancellationTokenSource = new CancellationTokenSource();

            if (DeleteTokenCheckBox != null)
                DeleteTokenCheckBox.IsChecked = false;
            if (DeleteDocumentsCheckBox != null)
                DeleteDocumentsCheckBox.IsChecked = false;

            this.Title = "LatticeVeil Uninstaller - Loading...";
            Debug.WriteLine("MainWindow constructor: Initializing uninstaller");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Initializing uninstaller");

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                Debug.WriteLine("MainWindow constructor: Shift key detected, enabling debug mode");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Shift key detected, enabling debug mode");
                EnableDebugMode();
            }

            this.MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) this.DragMove(); };

            if (NextBtn != null)
                NextBtn.IsEnabled = false;

            LoadInstallInformation();
            _ = Task.Run(async () => { await InitializeUninstallerAsync(); });
        }

        private void EnableDebugMode()
        {
            string exeDir = AppContext.BaseDirectory;
            string logPath = Path.Combine(exeDir, "installer_debug.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.AutoFlush = true;
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG LOG STARTED IN {exeDir} ===");

            Debug.WriteLine("=== DEBUG MODE ENABLED ===");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG MODE ENABLED ===");
            Debug.WriteLine($"Uninstaller Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Uninstaller Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            Debug.WriteLine($"Launch Time: {DateTime.Now}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Launch Time: {DateTime.Now}");
            Debug.WriteLine($"Working Directory: {Environment.CurrentDirectory}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Working Directory: {Environment.CurrentDirectory}");

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Debug Mode Enabled - Detailed logging active", "Debug Info", MessageBoxButton.OK, MessageBoxImage.Information);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Debug message shown to user");
            });
        }

        private void LoadInstallInformation()
        {
            try
            {
                string fallbackInstallPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string registryInstallPath = fallbackInstallPath;
                string version = "Unknown";
                string releaseTag = "";

                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil");
                if (key != null)
                {
                    registryInstallPath = key.GetValue("InstallLocation")?.ToString() ?? fallbackInstallPath;
                    version = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
                    releaseTag = key.GetValue("ReleaseTag")?.ToString() ?? "";
                }
                else
                {
                    // Fall back to the install root recorded by the installer.
                    var envRoot = Environment.GetEnvironmentVariable(InstallRootEnvVar);
                    if (!string.IsNullOrWhiteSpace(envRoot))
                        registryInstallPath = envRoot.Trim();
                }

                var metadata = ReadInstallMetadata(registryInstallPath);
                if (metadata.TryGetValue("InstallLocation", out var metadataInstallLocation) && !string.IsNullOrWhiteSpace(metadataInstallLocation))
                    registryInstallPath = metadataInstallLocation;
                if (metadata.TryGetValue("DisplayVersion", out var metadataDisplayVersion) && !string.IsNullOrWhiteSpace(metadataDisplayVersion))
                    version = metadataDisplayVersion;
                else if (metadata.TryGetValue("ReleaseTag", out var metadataReleaseTag) && !string.IsNullOrWhiteSpace(metadataReleaseTag))
                    releaseTag = metadataReleaseTag;

                if (version == "Unknown" && !string.IsNullOrWhiteSpace(releaseTag))
                    version = releaseTag;

                _installPath = registryInstallPath;
                _documentsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil");
                _backupRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeilWorldBackups");
                _installedVersion = version;
                _isNewLayout = IsNewInstallLayout(_installPath, metadata);

                Dispatcher.Invoke(() =>
                {
                    this.Title = $"LatticeVeil Uninstaller - {_installedVersion}";
                    if (VersionIndicator != null)
                        VersionIndicator.Text = _installedVersion == "Unknown" ? "" : _installedVersion;
                    if (BackupPathBox != null)
                        BackupPathBox.Text = _backupRootPath;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadInstallInformation Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadInstallInformation Error: {ex.Message}");
                _installPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                _documentsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil");
                _backupRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeilWorldBackups");
                _installedVersion = "Unknown";
            }
        }

        private Dictionary<string, string> ReadInstallMetadata(string installPath)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string[] candidatePaths =
                {
                    Path.Combine(installPath, InstallMetadataFile),
                    Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), InstallMetadataFile)
                };

                foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(candidatePath))
                        continue;

                    foreach (var line in File.ReadAllLines(candidatePath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        int separatorIndex = line.IndexOf('=');
                        if (separatorIndex <= 0)
                            continue;

                        var key = line.Substring(0, separatorIndex).Trim();
                        var value = line.Substring(separatorIndex + 1).Trim();
                        metadata[key] = value;
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadInstallMetadata Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ReadInstallMetadata Error: {ex.Message}");
            }

            return metadata;
        }

        private static bool IsNewInstallLayout(string installPath, Dictionary<string, string> metadata)
        {
            if (metadata.TryGetValue("Layout", out var layout) &&
                string.Equals(layout.Trim(), CurrentMetadataLayout, StringComparison.OrdinalIgnoreCase))
                return true;

            return Directory.Exists(Path.Combine(installPath, VersionsFolderName)) ||
                   Directory.Exists(Path.Combine(installPath, LauncherFolderName));
        }

        private async Task InitializeUninstallerAsync()
        {
            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            await UpdateSplashMessagesAsync(cancellationToken);
            await UpdateInitializationProgressAsync(25, "Reading installed version...");
            await UpdateInitializationProgressAsync(60, "Loading uninstall options...");
            await UpdateInitializationProgressAsync(100, "Uninstaller ready.");

            Dispatcher.Invoke(() =>
            {
                _isInitialized = true;
                ShowPage("Options");
            });
        }

        private async Task UpdateSplashMessagesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string splashMessage = _splashMessages[_random.Next(_splashMessages.Count)];
            Dispatcher.Invoke(() =>
            {
                if (SplashMessage != null)
                    SplashMessage.Text = splashMessage;
            });
            await Task.Delay(350, cancellationToken);
        }

        private Task UpdateInitializationProgressAsync(double progressValue, string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (InitProgress != null)
                    InitProgress.Value = progressValue;
                if (InitializationStatus != null)
                    InitializationStatus.Text = status;
            });

            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Initialization: {progressValue:F0}% - {status}");
            return Task.Delay(150);
        }

        private void UpdateNavigationButtons()
        {
            if (PreviousBtn == null || NextBtn == null || CancelBtn == null || NavigationBorder == null)
                return;

            PreviousBtn.IsEnabled = false;
            PreviousBtn.Visibility = Visibility.Visible;
            NextBtn.Visibility = Visibility.Visible;
            CancelBtn.Visibility = Visibility.Visible;
            NavigationBorder.Visibility = Visibility.Visible;

            switch (_currentPage)
            {
                case 0:
                    PreviousBtn.Visibility = Visibility.Hidden;
                    NextBtn.IsEnabled = false;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 1:
                    PreviousBtn.Visibility = Visibility.Hidden;
                    NextBtn.IsEnabled = _isInitialized;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 2:
                    PreviousBtn.IsEnabled = true;
                    NextBtn.IsEnabled = true;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 3:
                    PreviousBtn.IsEnabled = true;
                    NextBtn.IsEnabled = true;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "UNINSTALL";
                    break;
                case 4:
                case 5:
                    NavigationBorder.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ShowPage(string pageName)
        {
            if (InitializingPage != null)
                InitializingPage.Visibility = Visibility.Collapsed;
            if (OptionsPage != null)
                OptionsPage.Visibility = Visibility.Collapsed;
            if (WorldBackupPage != null)
                WorldBackupPage.Visibility = Visibility.Collapsed;
            if (ConfirmationPage != null)
                ConfirmationPage.Visibility = Visibility.Collapsed;
            if (ProgressPage != null)
                ProgressPage.Visibility = Visibility.Collapsed;
            if (CompletePage != null)
                CompletePage.Visibility = Visibility.Collapsed;

            switch (pageName)
            {
                case "Initializing":
                    if (InitializingPage != null)
                        InitializingPage.Visibility = Visibility.Visible;
                    _currentPage = 0;
                    break;
                case "Options":
                    if (OptionsPage != null)
                        OptionsPage.Visibility = Visibility.Visible;
                    _currentPage = 1;
                    break;
                case "WorldBackup":
                    if (WorldBackupPage != null)
                        WorldBackupPage.Visibility = Visibility.Visible;
                    _currentPage = 2;
                    break;
                case "Confirmation":
                    if (ConfirmationPage != null)
                        ConfirmationPage.Visibility = Visibility.Visible;
                    _currentPage = 3;
                    break;
                case "Progress":
                    if (ProgressPage != null)
                        ProgressPage.Visibility = Visibility.Visible;
                    _currentPage = 4;
                    break;
                case "Complete":
                    if (CompletePage != null)
                        CompletePage.Visibility = Visibility.Visible;
                    _currentPage = 5;
                    break;
            }

            UpdateNavigationButtons();
        }

        private void PreviousBtn_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentPage)
            {
                case 2:
                    ShowPage("Options");
                    break;
                case 3:
                    if (_hasWorldsAvailable)
                        ShowPage("WorldBackup");
                    else
                        ShowPage("Options");
                    break;
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            CancelBtn_Click(sender, e);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentPage)
            {
                case 1:
                    LoadWorlds();
                    if (_hasWorldsAvailable)
                    {
                        ShowPage("WorldBackup");
                    }
                    else
                    {
                        ShowConfirmationSummary();
                        ShowPage("Confirmation");
                    }
                    break;
                case 2:
                    if (_hasWorldsAvailable && !_worlds.Any(w => w.Checked))
                    {
                        var result = MessageBox.Show("ARE YOU SURE YOU DO NOT WANT TO BACKUP WORLDS?",
                            "World Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        if (result != MessageBoxResult.Yes)
                            return;
                    }
                    else if (_worlds.Any(w => w.Checked) && !_backupLocationSelected && !PromptForBackupLocation())
                    {
                        return;
                    }

                    ShowConfirmationSummary();
                    ShowPage("Confirmation");
                    break;
                case 3:
                    if (!ValidateBackupPath(_backupRootPath))
                    {
                        _backupLocationSelected = false;
                        ShowPage("WorldBackup");
                        return;
                    }

                    ShowPage("Progress");
                    bool deleteLoginToken = DeleteTokenCheckBox.IsChecked == true;
                    bool deleteDocumentsFolder = DeleteDocumentsCheckBox.IsChecked == true;
                    string backupRootPath = _backupRootPath;
                    var selectedWorlds = _worlds.Where(w => w.Checked).ToList();
                    _ = Task.Run(async () => { await PerformUninstallAsync(deleteLoginToken, deleteDocumentsFolder, backupRootPath, selectedWorlds); });
                    break;
            }
        }

        private void LoadWorlds()
        {
            _worlds.Clear();
            WorldsList.Children.Clear();
            _hasWorldsAvailable = false;
            _backupLocationSelected = false;

            var worldsPath = Path.Combine(_documentsPath, "Worlds");
            if (!Directory.Exists(worldsPath))
            {
                WorldBackupDescription.Text = "No worlds were found in Documents\\LatticeVeil\\Worlds.";
                BackupAllBtn.IsEnabled = false;
                WorldsList.Children.Add(CreateEmptyWorldMessage("No worlds were found to back up."));
                return;
            }

            foreach (var worldDir in Directory.GetDirectories(worldsPath))
            {
                var worldInfo = LoadWorldInfo(worldDir);
                if (worldInfo != null)
                {
                    _worlds.Add(worldInfo);
                    WorldsList.Children.Add(CreateWorldControl(worldInfo));
                }
            }

            if (_worlds.Count == 0)
            {
                WorldBackupDescription.Text = "No valid worlds were found in Documents\\LatticeVeil\\Worlds.";
                BackupAllBtn.IsEnabled = false;
                WorldsList.Children.Add(CreateEmptyWorldMessage("No valid worlds were found to back up."));
            }
            else
            {
                WorldBackupDescription.Text = "Select any worlds you want backed up before LatticeVeil is removed. You will choose the backup folder after clicking NEXT.";
                BackupAllBtn.IsEnabled = true;
                _hasWorldsAvailable = true;
            }
        }

        private Border CreateEmptyWorldMessage(string message)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private WorldInfo? LoadWorldInfo(string worldDir)
        {
            try
            {
                var worldFile = Path.Combine(worldDir, "world.lvc");
                if (!File.Exists(worldFile))
                    return null;

                var worldInfo = new WorldInfo
                {
                    Directory = worldDir,
                    Checked = false,
                    PreviewPath = Path.Combine(worldDir, "preview.png"),
                    Name = Path.GetFileName(worldDir)
                };

                var manifest = ReadWorldManifest(worldFile);

                if (TryGetManifestValue(manifest, out var name, "Name", "World.Name"))
                    worldInfo.Name = name;
                if (TryGetManifestValue(manifest, out var mode, "CurrentWorldGameMode", "Modes.current_mode", "GameMode", "Modes.game_mode", "InitialGameMode", "Modes.initial_mode"))
                    worldInfo.GameMode = NormalizeGameMode(mode);
                if (TryGetManifestValue(manifest, out var seed, "Seed", "World.seed"))
                    worldInfo.Seed = seed;
                if (TryGetManifestValue(manifest, out var cheats, "Gameplay.EnableCheats", "Gameplay.cheats", "EnableCheats", "cheats_enabled"))
                    worldInfo.EnableCheats = ParseManifestBool(cheats);
                if (TryGetManifestValue(manifest, out var worldType, "WorldGeneration.WorldType", "World.world_type", "WorldType", "world_type"))
                    worldInfo.WorldType = NormalizeWorldType(worldType);

                return worldInfo;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadWorldInfo Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadWorldInfo Error: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, string> ReadWorldManifest(string worldFile)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? section = null;

            foreach (var rawLine in File.ReadAllLines(worldFile))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                    continue;

                var rawKey = line.Substring(0, separatorIndex).Trim();
                if (string.IsNullOrWhiteSpace(rawKey))
                    continue;

                var value = UnquoteManifestValue(line.Substring(separatorIndex + 1).Trim());
                values[rawKey] = value;

                if (!string.IsNullOrWhiteSpace(section))
                {
                    values[$"{section}.{rawKey}"] = value;
                    var mappedKey = MapWorldManifestKey(section, rawKey);
                    if (!string.IsNullOrWhiteSpace(mappedKey))
                        values[mappedKey] = value;
                }
            }

            return values;
        }

        private static string MapWorldManifestKey(string section, string key)
        {
            var normalizedSection = section.Trim().ToUpperInvariant();
            var normalizedKey = key.Trim().ToLowerInvariant();

            return normalizedSection switch
            {
                "WORLD" => normalizedKey switch
                {
                    "name" => "Name",
                    "seed" => "Seed",
                    "world_type" => "WorldGeneration.WorldType",
                    _ => string.Empty
                },
                "MODES" => normalizedKey switch
                {
                    "game_mode" => "GameMode",
                    "initial_mode" => "InitialGameMode",
                    "current_mode" => "CurrentWorldGameMode",
                    _ => string.Empty
                },
                "GAMEPLAY" => normalizedKey switch
                {
                    "cheats" => "Gameplay.EnableCheats",
                    _ => string.Empty
                },
                _ => string.Empty
            };
        }

        private static bool TryGetManifestValue(Dictionary<string, string> manifest, out string value, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (manifest.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
                    return true;
            }

            value = string.Empty;
            return false;
        }

        private static string UnquoteManifestValue(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return value;
        }

        private static bool ParseManifestBool(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGameMode(string value)
        {
            var token = (value ?? string.Empty).Trim();
            return token.ToLowerInvariant() switch
            {
                "creative" => "Artificer",
                "survival" => "Veilwalker",
                "spectator" => "Veilseer",
                "artificer" => "Artificer",
                "veilwalker" => "Veilwalker",
                "veilseer" => "Veilseer",
                _ => string.IsNullOrWhiteSpace(token) ? "Unknown" : token
            };
        }

        private static string NormalizeWorldType(string value)
        {
            var token = (value ?? string.Empty).Trim();
            return token.ToLowerInvariant() switch
            {
                "terrain" => "Terrain",
                "flatlands" => "Flatlands",
                "flat" => "Flatlands",
                "normal" => "Terrain",
                _ => string.IsNullOrWhiteSpace(token) ? "Unknown" : token
            };
        }

        private Border CreateWorldControl(WorldInfo world)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14)
            };

            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var checkBox = new CheckBox
            {
                IsChecked = world.Checked,
                Style = (Style)FindResource("ModernCheckBox"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            checkBox.Checked += (s, e) => world.Checked = true;
            checkBox.Unchecked += (s, e) => world.Checked = false;
            world.SelectionCheckBox = checkBox;
            Grid.SetColumn(checkBox, 0);

            var image = new Image
            {
                Width = 96,
                Height = 96,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 16, 0),
                Source = LoadWorldPreview(world)
            };
            Grid.SetColumn(image, 1);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = world.Name, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16 });
            textStack.Children.Add(new TextBlock { Text = $"GameMode: {world.GameMode}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 6, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"Seed: {world.Seed}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"EnableCheats: {world.EnableCheats}", Foreground = world.EnableCheats ? new SolidColorBrush(Color.FromRgb(255, 165, 0)) : new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"WorldType: {world.WorldType}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(textStack, 2);

            rootGrid.Children.Add(checkBox);
            rootGrid.Children.Add(image);
            rootGrid.Children.Add(textStack);
            border.Child = rootGrid;
            return border;
        }

        private ImageSource? LoadWorldPreview(WorldInfo world)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = File.Exists(world.PreviewPath) ? new Uri(world.PreviewPath, UriKind.Absolute) : new Uri("pack://application:,,,/Icon.ico", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void BackupAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var world in _worlds)
            {
                world.Checked = true;
                if (world.SelectionCheckBox != null)
                    world.SelectionCheckBox.IsChecked = true;
            }
        }

        private void ShowConfirmationSummary()
        {
            var selectedWorlds = _worlds.Where(w => w.Checked).ToList();
            var backupRootPath = _backupRootPath;
            var summary = "The following actions will be performed:\n\n";
            summary += "• Remove LatticeVeil application files\n";
            summary += "• Remove shortcuts\n";
            summary += "• Remove uninstall registry entries\n";

            if (_isNewLayout)
                summary += "• Remove installed game versions from the install root\n";

            if (DeleteTokenCheckBox.IsChecked == true)
                summary += "• Delete account login token and auth cache\n";

            if (selectedWorlds.Count > 0)
                summary += $"• Backup {selectedWorlds.Count} world(s) as .lvworld\n";
            if (selectedWorlds.Count > 0)
                summary += $"• Save backups to: {backupRootPath}\n";

            if (DeleteDocumentsCheckBox.IsChecked == true)
                summary += "• Delete Documents\\LatticeVeil folder entirely (including all worlds and saves)\n";

            ConfirmationText.Text = summary;
        }

        private async Task PerformUninstallAsync(bool deleteLoginToken, bool deleteDocumentsFolder, string backupRootPath, List<WorldInfo> selectedWorlds)
        {
            try
            {
                await UpdateProgressAsync(5, "Preparing to uninstall...");

                if (deleteLoginToken)
                {
                    await UpdateProgressAsync(15, "Deleting account login token and auth cache...");
                    DeleteLoginToken();
                }

                if (selectedWorlds.Count > 0)
                {
                    await UpdateProgressAsync(35, "Backing up selected worlds...");
                    BackupSelectedWorlds(selectedWorlds, backupRootPath);
                }

                await UpdateProgressAsync(55, "Removing shortcuts...");
                DeleteShortcuts();

                await UpdateProgressAsync(65, "Removing protocol registration...");
                DeleteProtocolRegistration();

                await UpdateProgressAsync(70, "Removing registry entries...");
                DeleteRegistryEntries();

                await UpdateProgressAsync(85, "Removing installed files...");
                DeleteInstalledFiles();

                await UpdateProgressAsync(90, "Clearing runtime cache...");
                DeleteRuntimeCache(deleteLoginToken);

                if (deleteDocumentsFolder)
                {
                    await UpdateProgressAsync(95, "Deleting Documents\\LatticeVeil...");
                    DeleteDocumentsFolder();
                }

                await UpdateProgressAsync(100, "Uninstallation complete!");
                _uninstallCompleted = true;
                Dispatcher.Invoke(() => ShowPage("Complete"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PerformUninstallAsync Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] PerformUninstallAsync Error: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Uninstallation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowPage("Confirmation");
                });
            }
        }

        private Task UpdateProgressAsync(double value, string status)
        {
            Dispatcher.Invoke(() =>
            {
                UninstallProgressBar.Value = value;
                UninstallProgressText.Text = $"{value:F0}%";
                UninstallStatus.Text = status;
            });

            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Progress: {value:F0}% - {status}");
            return Task.Delay(250);
        }

        private void DeleteLoginToken()
        {
            try
            {
                var tokenPath = Path.Combine(_installPath, "login_token.json");
                if (File.Exists(tokenPath))
                {
                    File.Delete(tokenPath);
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted login token at {tokenPath}");
                }

                var appDataTokenPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LatticeVeil",
                    "veilnet_launcher_token.json");
                if (File.Exists(appDataTokenPath))
                {
                    File.Delete(appDataTokenPath);
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted launcher auth token at {appDataTokenPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteLoginToken Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteLoginToken Error: {ex.Message}");
            }
        }

        private void DeleteRuntimeCache(bool deleteAccountAuth)
        {
            try
            {
                var roamingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LatticeVeil");
                var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatticeVeil");
                DeleteDirectoryIfExists(Path.Combine(roamingRoot, "Runtime"), "runtime cache");
                DeleteDirectoryIfExists(localRoot, "legacy local app cache");

                if (deleteAccountAuth)
                    DeleteDirectoryIfExists(Path.Combine(roamingRoot, "System"), "account auth cache");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteRuntimeCache Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteRuntimeCache Error: {ex.Message}");
            }
        }

        private static void DeleteDirectoryIfExists(string path, string label)
        {
            if (!Directory.Exists(path))
                return;

            Directory.Delete(path, true);
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted {label} at {path}");
        }

        private void BackupSelectedWorlds(List<WorldInfo> selectedWorlds, string backupRootPath)
        {
            Directory.CreateDirectory(backupRootPath);

            foreach (var world in selectedWorlds)
            {
                var backupPath = Path.Combine(backupRootPath, $"{SanitizeWorldName(world.Name)}.lvworld");
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                ZipFile.CreateFromDirectory(world.Directory, backupPath, CompressionLevel.Optimal, false);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Backed up world to {backupPath}");
            }
        }

        private void BrowseBackupPathBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select where world backups should be saved",
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_backupRootPath)
                    ? _backupRootPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _backupRootPath = dialog.SelectedPath;
                _backupLocationSelected = true;
                if (BackupPathBox != null)
                    BackupPathBox.Text = _backupRootPath;
            }
        }

        private bool PromptForBackupLocation()
        {
            BrowseBackupPathBtn_Click(this, new RoutedEventArgs());
            if (!_backupLocationSelected)
                return false;

            if (ValidateBackupPath(_backupRootPath))
                return true;

            _backupLocationSelected = false;
            return false;
        }

        private bool ValidateBackupPath(string backupPath)
        {
            if (!_worlds.Any(w => w.Checked))
                return true;

            backupPath = backupPath.Trim();
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                MessageBox.Show("Please choose a valid backup folder.", "Invalid Backup Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (DeleteDocumentsCheckBox.IsChecked == true)
            {
                string normalizedBackupPath = Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedDocumentsPath = Path.GetFullPath(_documentsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedBackupPath.StartsWith(normalizedDocumentsPath, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Choose a backup folder outside Documents\\LatticeVeil if you plan to delete that folder.", "Invalid Backup Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            _backupRootPath = backupPath;
            return true;
        }

        private void DeleteRegistryEntries()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil", false);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted uninstall registry entries");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteRegistryEntries Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteRegistryEntries Error: {ex.Message}");
            }
        }

        private void DeleteShortcuts()
        {
            try
            {
                var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LatticeVeil.lnk");
                if (File.Exists(desktopPath))
                    File.Delete(desktopPath);

                var startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LatticeVeil");
                if (Directory.Exists(startMenuPath))
                    Directory.Delete(startMenuPath, true);

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted shortcuts");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteShortcuts Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteShortcuts Error: {ex.Message}");
            }
        }

        private void DeleteProtocolRegistration()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\latticeveil", false);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted protocol registration");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteProtocolRegistration Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteProtocolRegistration Error: {ex.Message}");
            }
        }

        private void DeleteInstalledFiles()
        {
            try
            {
                if (!Directory.Exists(_installPath))
                    return;

                if (_isNewLayout)
                {
                    DeleteNewLayoutFiles();
                    return;
                }

                // Safety check: only proceed if we're in a LatticeVeil installation directory
                if (!IsLatticeVeilInstallDirectory(_installPath))
                {
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Safety check failed: {_installPath} is not a valid LatticeVeil installation directory");
                    return;
                }

                // Delete only LatticeVeil-specific files
                var latticeVeilFiles = new[]
                {
                    "LatticeVeilMonoGame.exe",
                    "LatticeVeilLauncher.exe", 
                    "latticeveil_install_info.txt",
                    "login_token.json",
                    "eos.public.json"
                };

                foreach (var file in Directory.GetFiles(_installPath))
                {
                    var fileName = Path.GetFileName(file);
                    
                    // Skip the uninstaller itself
                    if (string.Equals(fileName, "LatticeVeilUninstaller.exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // Delete known LatticeVeil files
                    if (latticeVeilFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted LatticeVeil file: {fileName}");
                    }
                }

                // Delete only LatticeVeil-specific directories
                var latticeVeilDirectories = new[]
                {
                    "LatticeVeilMonoGame",
                    "LatticeVeilMonoGame_Data",
                    "eos",
                    "Defaults"
                };

                foreach (var dir in Directory.GetDirectories(_installPath))
                {
                    var dirName = Path.GetFileName(dir);
                    
                    // Only delete known LatticeVeil directories
                    if (latticeVeilDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        Directory.Delete(dir, true);
                        Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted LatticeVeil directory: {dirName}");
                    }
                }

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted LatticeVeil files from {_installPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteInstalledFiles Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteInstalledFiles Error: {ex.Message}");
            }
        }

        /// <summary>Removes the new install-root layout: Versions\<tag>\, Launcher, root files.</summary>
        private void DeleteNewLayoutFiles()
        {
            try
            {
                if (!Directory.Exists(_installPath))
                    return;

                // Remove every installed game version (Versions\<tag>\).
                var versionsDir = Path.Combine(_installPath, VersionsFolderName);
                if (Directory.Exists(versionsDir))
                {
                    foreach (var versionDir in Directory.GetDirectories(versionsDir))
                    {
                        try
                        {
                            Directory.Delete(versionDir, true);
                            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted version folder: {Path.GetFileName(versionDir)}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DeleteNewLayoutFiles Error ({versionDir}): {ex.Message}");
                        }
                    }

                    try { Directory.Delete(versionsDir, true); } catch { /* best effort */ }
                }

                // Launcher subfolder (reserved for the future bundled launcher).
                DeleteDirectoryIfExists(Path.Combine(_installPath, LauncherFolderName), "launcher folder");

                // Known root-level files (never the uninstaller itself).
                foreach (var file in new[]
                {
                    GameExe,
                    InstallMetadataFile,
                    "login_token.json",
                    "eos.public.json"
                })
                {
                    var filePath = Path.Combine(_installPath, file);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted {file}");
                        }
                        catch { /* best effort */ }
                    }
                }

                // Clear the install-root environment variable when it points here.
                RemoveInstallRootEnvVar();

                // Remove the install root itself once nothing is left inside it.
                try
                {
                    if (Directory.Exists(_installPath) && !Directory.EnumerateFileSystemEntries(_installPath).Any())
                        Directory.Delete(_installPath);
                }
                catch { /* best effort */ }

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted new-layout files from {_installPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteNewLayoutFiles Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteNewLayoutFiles Error: {ex.Message}");
            }
        }

        private void RemoveInstallRootEnvVar()
        {
            try
            {
                var envRoot = Environment.GetEnvironmentVariable(InstallRootEnvVar);
                if (string.IsNullOrWhiteSpace(envRoot))
                    return;

                var normalizedEnvRoot = Path.GetFullPath(envRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedInstallPath = Path.GetFullPath(_installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(normalizedEnvRoot, normalizedInstallPath, StringComparison.OrdinalIgnoreCase))
                    return;

                using var envKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
                envKey?.DeleteValue(InstallRootEnvVar, throwOnMissingValue: false);
                BroadcastEnvironmentChange();
                Environment.SetEnvironmentVariable(InstallRootEnvVar, null);

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Cleared {InstallRootEnvVar} user environment variable");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RemoveInstallRootEnvVar Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RemoveInstallRootEnvVar Error: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private static void BroadcastEnvironmentChange()
        {
            try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0, 1000, out _); }
            catch { /* best effort */ }
        }

        private bool IsLatticeVeilInstallDirectory(string path)
        {
            try
            {
                // New layout: metadata plus Versions/Launcher folders under the install root.
                var metadataFile = Path.Combine(path, "latticeveil_install_info.txt");
                if (!File.Exists(metadataFile))
                    return false;

                if (Directory.Exists(Path.Combine(path, VersionsFolderName)) ||
                    Directory.Exists(Path.Combine(path, LauncherFolderName)))
                    return true;

                // Classic layout: game exe + uninstaller beside the metadata.
                var requiredFiles = new[]
                {
                    "LatticeVeilMonoGame.exe",
                    "LatticeVeilUninstaller.exe"
                };

                foreach (var file in requiredFiles)
                {
                    if (!File.Exists(Path.Combine(path, file)))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void DeleteDocumentsFolder()
        {
            try
            {
                if (Directory.Exists(_documentsPath))
                {
                    Directory.Delete(_documentsPath, true);
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted documents folder {_documentsPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteDocumentsFolder Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteDocumentsFolder Error: {ex.Message}");
            }
        }

        private void FinishBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_uninstallCompleted && !_cleanupScheduled)
            {
                _cleanupScheduled = true;
                ExtractAndRunCleanupBatch();
            }

            Close();
        }

        private void ExtractAndRunCleanupBatch()
        {
            try
            {
                var workingDirectory = Path.GetDirectoryName(_installPath);
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                // Only delete the uninstaller EXE itself, not the entire folder
                string uninstallerPath = Path.Combine(_installPath, "LatticeVeilUninstaller.exe").Replace("'", "''");
                string cleanupScript =
                    "$uninstaller = '" + uninstallerPath + "'; " +
                    "Start-Sleep -Seconds 2; " +
                    "for ($i = 0; $i -lt 10; $i++) { " +
                    "if (-not (Test-Path -LiteralPath $uninstaller)) { break } " +
                    "try { Remove-Item -LiteralPath $uninstaller -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 } " +
                    "}";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{cleanupScript}\"",
                        WorkingDirectory = workingDirectory,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };

                process.Start();
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Started uninstaller self-cleanup: {uninstallerPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractAndRunCleanupBatch Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ExtractAndRunCleanupBatch Error: {ex.Message}");
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to cancel the uninstallation?",
                "Cancel Uninstallation", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _cancellationTokenSource?.Cancel();
                Close();
            }
        }

        private string SanitizeWorldName(string worldName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitizedName = worldName;

            foreach (var invalidChar in invalidChars)
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }

            sanitizedName = sanitizedName.Trim('.', ' ');
            if (string.IsNullOrEmpty(sanitizedName))
                sanitizedName = "ImportedWorld";

            return sanitizedName;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            catch
            {
            }
            finally
            {
                base.OnClosed(e);
            }
        }
    }

    public class WorldInfo
    {
        public string Name { get; set; } = "Unknown World";
        public string GameMode { get; set; } = "Unknown";
        public string Seed { get; set; } = "Unknown";
        public string WorldType { get; set; } = "Unknown";
        public bool EnableCheats { get; set; }
        public string Directory { get; set; } = "";
        public string PreviewPath { get; set; } = "";
        public bool Checked { get; set; } = false;
        public CheckBox? SelectionCheckBox { get; set; }
    }
}
