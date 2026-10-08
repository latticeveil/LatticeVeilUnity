using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace LatticeVeil.Messages
{
    public partial class MainWindow : Window
    {
        // Currently displayed friend profile (right-side pane).
        private string _profileFriendId;

        // INotifySurface for the profile pane bindings (WPF needs public
        // properties; a minimal INPC impl keeps banner/avatar reactive).
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        string _profileBanner, _profilePicture, _profileInitial;
        public string ProfileBanner { get => _profileBanner; set { _profileBanner = value; Raise(nameof(ProfileBanner)); Raise(nameof(ProfileBannerVisible)); } }
        public string ProfilePicture { get => _profilePicture; set { _profilePicture = value; Raise(nameof(ProfilePicture)); Raise(nameof(ProfilePictureVisible)); } }
        public string ProfileInitial { get => _profileInitial; set { _profileInitial = value; Raise(nameof(ProfileInitial)); } }
        public Visibility ProfileBannerVisible => string.IsNullOrEmpty(_profileBanner) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ProfilePictureVisible => string.IsNullOrEmpty(_profilePicture) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ProfileInitialVisible => string.IsNullOrEmpty(_profilePicture) ? Visibility.Visible : Visibility.Collapsed;

        public static string ActiveConversation;
        public static bool IsHidden => !_isOpen || Application.Current == null || !Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible);
        private static bool _isOpen;

        private readonly ObservableCollection<ConvVm> _convs = new ObservableCollection<ConvVm>();
        private readonly ObservableCollection<MsgVm> _msgs = new ObservableCollection<MsgVm>();
        private readonly ObservableCollection<ConvVm> _friends = new ObservableCollection<ConvVm>();
        private string _current;
        private string _lastRenderedSig;
        private bool _onFriendsTab;
        private bool _pinned;
        public bool IsPinned => _pinned;
        private DispatcherTimer _uiTimer;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            _isOpen = true;
            ConvList.ItemsSource = _convs;
            MsgList.ItemsSource = _msgs;
            FriendList.ItemsSource = _friends;

            var svc = App.Service;
            if (svc == null || string.IsNullOrWhiteSpace(App.LauncherToken))
            {
                ChatTitle.Text = "Not signed in";
                ConnText.Text = "launch from the LatticeVeil launcher";
                ConvEmpty.Visibility = Visibility.Visible;
                MsgInput.IsEnabled = false;
                SendBtn.IsEnabled = false;
                return;
            }

            svc.StateChanged += s => Dispatcher.BeginInvoke(() => ConnText.Text = s == "on" ? "live" : s == "wait" ? "reconnecting…" : "offline");
            // Replay the CURRENT state: the socket may have connected before
            // this window subscribed, which left the label on "connecting…"
            // even though messaging was live.
            ConnText.Text = svc.ConnState == "on" ? "live"
                : svc.ConnState == "wait" ? "connecting…" : "offline";
            svc.DataChanged += () => Dispatcher.BeginInvoke(() =>
            {
                RefreshConvList();
                if (_onFriendsTab) RefreshFriendList();
            });
            svc.MessageArrived += convId => Dispatcher.BeginInvoke(() =>
            {
                RefreshConvList();
                if (ActiveConversation == convId) ReloadMessages(convId);
            });
            // Handler must exist BEFORE the first clear event can arrive.
            svc.ConversationCleared += (convId, by) => Dispatcher.BeginInvoke(() =>
            {
                RefreshConvList();
                if (ActiveConversation == convId) ReloadMessages(convId);
            });
            svc.ErrorOccurred += msg => Dispatcher.BeginInvoke(() =>
            {
                ErrText.Text = msg;
                ErrText.Visibility = Visibility.Visible;
            });

            // Initial load + keep unread badges/preview fresh.
            _ = Task.Run(async () =>
            {
                try { await svc.BootstrapAsync(); } catch (Exception ex) { ErrText.Dispatcher.BeginInvoke(() => { ErrText.Text = ex.Message; ErrText.Visibility = Visibility.Visible; }); }
            });
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _uiTimer.Tick += (s, e) =>
            {
                RefreshConvList();
                // Auto-refresh the open chat from the service cache — even if
                // an event was missed, rendered content can lag at most 2s.
                if (_current != null)
                {
                    var cached = App.Service?.GetCachedMessages(_current);
                    if (cached != null)
                    {
                        var sig = cached.Count + ":" + (cached.Count > 0 ? cached[cached.Count - 1].Id : "");
                        if (sig != _lastRenderedSig) ReloadMessages(_current);
                    }
                }
            };
            _uiTimer.Start();

            Closing += (s, e) =>
            {
                // Independent close: hide-and-detach by default (messaging keeps
                // running); only the X in a pinned window actually shuts down.
                if (_pinned && !_forceClose)
                {
                    e.Cancel = true;
                    Hide();
                }
                _isOpen = Application.Current != null && Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && w.IsVisible);
            };
        }

        private bool _forceClose;

        protected override void OnClosed(EventArgs e)
        {
            _isOpen = false;
            base.OnClosed(e);
        }

        // ---------------- pin / sound / window controls ----------------
        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            _pinned = !_pinned;
            Topmost = _pinned;
            PinBtn.Content = _pinned ? "\U0001F4CC\u2713" : "\U0001F4CC";
            PinBtn.Opacity = _pinned ? 1.0 : 0.55;
            PinBtn.ToolTip = _pinned ? "Unpin (stop staying on top)" : "Pin this window on top (stay open)";

            // Owned windows hide when the launcher minimizes; a PINNED window
            // must stay open separately, so pinning detaches ownership (and
            // gives it a taskbar entry while standalone). Unpinning re-attaches
            // it to the launcher (no taskbar entry again).
            if (_ownedByLauncher || _pinned)
            {
                try
                {
                    var me = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (me != IntPtr.Zero)
                    {
                        if (_pinned)
                        {
                            // Detach: taskbar entry back first (WPF re-owns to
                            // its hidden window), then clear the native owner.
                            ShowInTaskbar = true;
                            SetWindowOwnerHwnd(me, IntPtr.Zero);
                        }
                        else
                        {
                            if (_launcherOwner != IntPtr.Zero && IsWindow(_launcherOwner))
                            {
                                ShowInTaskbar = false;
                                SetWindowOwnerHwnd(me, _launcherOwner);
                            }
                            else ShowInTaskbar = true;
                        }
                    }
                }
                catch { /* keep whatever state is safe */ }
            }
        }

        private void SoundBtn_Click(object sender, RoutedEventArgs e)
        {
            var svc = App.Service;
            if (svc == null) return;
            svc.SoundEnabled = !svc.SoundEnabled;
            SoundBtn.Content = svc.SoundEnabled ? "\U0001F50A" : "\U0001F507";
            SoundBtn.Opacity = svc.SoundEnabled ? 1.0 : 0.55;
            SoundBtn.ToolTip = "Sound: " + (svc.SoundEnabled ? "ON" : "OFF");
        }

        // When owned by the launcher there is no taskbar entry to restore
        // from, so "minimize" tucks the window away; the launcher's MESSAGES
        // button brings it back (messaging keeps running either way).
        private bool _ownedByLauncher;

        private void MinBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_ownedByLauncher) Hide();
            else WindowState = WindowState.Minimized;
        }

        // ---------------- launcher ownership (one taskbar entry) ----------------
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_HWNDPARENT = -8;

        private static IntPtr SetWindowOwnerHwnd(IntPtr hWnd, IntPtr owner)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, GWL_HWNDPARENT, owner);
            return new IntPtr(SetWindowLong32(hWnd, GWL_HWNDPARENT, owner.ToInt32()));
        }

        // The launcher window we attached to (explicit handoff or discovered
        // from the parent process) — reused when unpinning re-attaches.
        private IntPtr _launcherOwner;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            AttachToLauncherIfAvailable();
        }

        /// <summary>
        /// Attaches this window to the launcher so there is exactly ONE
        /// taskbar entry. Resolution order:
        ///   1. explicit handoff (LV_LAUNCHER_HWND from new launcher builds)
        ///   2. self-discovery: our PARENT process is the LatticeVeil
        ///      launcher — own its main window. This works with the launcher
        ///      as shipped today, no rebuild needed.
        /// If neither applies (genuinely standalone), normal taskbar
        /// behavior is kept.
        /// </summary>
        private void AttachToLauncherIfAvailable()
        {
            try
            {
                if (long.TryParse(Environment.GetEnvironmentVariable("LV_LAUNCHER_HWND"), out var l) && l != 0)
                {
                    var h = new IntPtr(l);
                    if (IsWindow(h)) { AttachTo(h); return; }
                }

                var parentPid = GetParentProcessId();
                if (parentPid <= 0) return;
                using (var parent = System.Diagnostics.Process.GetProcessById(parentPid))
                {
                    if (parent == null) return;
                    var name = parent.ProcessName ?? "";
                    if (name.Equals("LatticeVeilMessages", StringComparison.OrdinalIgnoreCase)) return;
                    if (!name.StartsWith("LatticeVeil", StringComparison.OrdinalIgnoreCase)) return;
                    var hwnd = parent.MainWindowHandle;
                    if (hwnd != IntPtr.Zero && IsWindow(hwnd)) AttachTo(hwnd);
                }
            }
            catch { /* standalone fallback: normal taskbar behavior */ }
        }

        private void AttachTo(IntPtr owner)
        {
            var me = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (me == IntPtr.Zero) return;
            // ORDER MATTERS: ShowInTaskbar=false makes WPF re-own the window
            // to its internal hidden window, which would CLOBBER the native
            // launcher ownership. Hide from the taskbar first, then attach.
            ShowInTaskbar = false;
            SetWindowOwnerHwnd(me, owner);
            _launcherOwner = owner;
            _ownedByLauncher = true;
            MinBtn.ToolTip = "Tuck away (reopen from the launcher's MESSAGES button)";
        }

        // ---------------- parent process discovery ----------------
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public System.IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern System.IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool Process32FirstW(System.IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool Process32NextW(System.IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(System.IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        private const uint TH32CS_SNAPPROCESS = 0x2;

        private static int GetParentProcessId()
        {
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == System.IntPtr.Zero || snap == (System.IntPtr)(-1)) return 0;
            try
            {
                var pe = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                int me = GetCurrentProcessId();
                if (Process32FirstW(snap, ref pe))
                {
                    do
                    {
                        if ((int)pe.th32ProcessID == me) return (int)pe.th32ParentProcessID;
                    } while (Process32NextW(snap, ref pe));
                }
                return 0;
            }
            finally { CloseHandle(snap); }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_pinned)
            {
                // Pinned: close = fully exit messaging (independent of launcher).
                _forceClose = true;
                Close();
            }
            else
            {
                // Unpinned: hide to tray-like background; messaging keeps running.
                Hide();
            }
        }

        // ---------------- conversations ----------------
        // NOTE: WPF {Binding} resolves public PROPERTIES (not fields) — these
        // were fields once and every template bound to nothing.
        public sealed class ConvVm
        {
            public string ConversationId { get; set; }
            public string OtherId { get; set; }
            public string Title { get; set; }
            public string Preview { get; set; }
            public string Initial { get; set; }
            public string Picture { get; set; }
            public Brush AvatarBrush { get; set; }
            public int Unread { get; set; }
            // Friends-tab profile card fields
            public string Banner { get; set; }
            public string About { get; set; }
            public Visibility UnreadVisible => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
            public string UnreadText => Unread > 99 ? "99+" : (Unread > 0 ? Unread.ToString() : "");
            public Visibility PictureVisible => string.IsNullOrEmpty(Picture) ? Visibility.Collapsed : Visibility.Visible;
            public Visibility InitialVisible => string.IsNullOrEmpty(Picture) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility BannerVisible => string.IsNullOrEmpty(Banner) ? Visibility.Collapsed : Visibility.Visible;
            public Visibility AboutVisible => string.IsNullOrEmpty(About) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Frozen ImageBrush per avatar URL (thread-safe UI-side cache).
        private static readonly Dictionary<string, Brush> AvatarBrushes
            = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        private static Brush GetAvatarBrush(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (AvatarBrushes)
            {
                if (AvatarBrushes.TryGetValue(url, out var brush)) return brush;
            }
            Brush result = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnDemand;
                bmp.UriSource = new Uri(url, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                result = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { result = null; }
            lock (AvatarBrushes) { AvatarBrushes[url] = result; }
            return result;
        }

        private void RefreshConvList()
        {
            var svc = App.Service;
            if (svc == null) return;
            // Chats tab shows only people you actually have messages with —
            // "chatted with" (a conversation with no messages yet is
            // reachable through the Friends tab instead, mirroring the site).
            var snapshot = svc.Conversations
                .Where(c => !string.IsNullOrEmpty(c.LastAt))
                .OrderByDescending(c => c.LastAt ?? "")
                .ToList();
            // An ACTIVE draft thread row (friend with no messages yet, chosen
            // from the profile pane) survives refreshes until first send;
            // once messages exist the real conversation row takes over.
            ConvVm draftRow = null;
            if (!string.IsNullOrEmpty(_draftFriendIdActive) && _current == null)
            {
                var draftConv = svc.Conversations.FirstOrDefault(c => c.OtherId == _draftFriendIdActive && !string.IsNullOrEmpty(c.LastAt));
                if (draftConv == null && !snapshot.Any(c => c.OtherId == _draftFriendIdActive))
                {
                    App.Service.Profiles.TryGetValue(_draftFriendIdActive, out var dp);
                    var dn = string.IsNullOrEmpty(dp.username)
                        ? (_draftFriendIdActive.Length > 8 ? _draftFriendIdActive.Substring(0, 8) : _draftFriendIdActive)
                        : dp.username;
                    draftRow = new ConvVm
                    {
                        ConversationId = null,
                        OtherId = _draftFriendIdActive,
                        Title = dn,
                        Preview = "New chat — say hi!",
                        Initial = string.IsNullOrEmpty(dn) ? "?" : dn.Substring(0, 1).ToUpperInvariant(),
                        Picture = dp.picture,
                        AvatarBrush = GetAvatarBrush(dp.picture),
                    };
                }
            }
            _convs.Clear();
            foreach (var c in snapshot)
            {
                // Prefer the authoritative username/picture from the shared
                // Profiles table (bootstrap fills it for friends AND others).
                if (string.IsNullOrEmpty(c.OtherUsername) || string.IsNullOrEmpty(c.OtherPicture))
                {
                    if (svc.Profiles.TryGetValue(c.OtherId, out var prof))
                    {
                        if (string.IsNullOrEmpty(c.OtherUsername)) c.OtherUsername = prof.username ?? "";
                        if (string.IsNullOrEmpty(c.OtherPicture)) c.OtherPicture = prof.picture ?? "";
                    }
                }
                var name = string.IsNullOrEmpty(c.OtherUsername)
                    ? (c.OtherId.Length > 8 ? c.OtherId.Substring(0, 8) : c.OtherId)
                    : c.OtherUsername;
                _convs.Add(new ConvVm
                {
                    ConversationId = c.ConversationId,
                    OtherId = c.OtherId,
                    Title = name,
                    Preview = c.LastBody ?? "",
                    Initial = (string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1)).ToUpperInvariant(),
                    Picture = c.OtherPicture,
                    AvatarBrush = GetAvatarBrush(c.OtherPicture),
                    Unread = c.Unread,
                });
            }
            if (draftRow != null) _convs.Insert(0, draftRow);
            ConvEmpty.Visibility = _convs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ConvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConvList.SelectedItem is ConvVm vm)
            {
                if (vm.ConversationId == null)
                {
                    // Draft thread row clicked: restore draft composition.
                    _current = null;
                    _draftFriendIdActive = vm.OtherId;
                    _draftBackingConvId = null;
                    ActiveConversation = null;
                    ChatTitle.Text = vm.Title;
                    _msgs.Clear();
                    ClearedText.Visibility = Visibility.Collapsed;
                    DelChatBtn.Visibility = Visibility.Collapsed;
                    MsgInput.IsEnabled = true;
                    SendBtn.IsEnabled = true;
                    _lastRenderedSig = "draft:0";
                    return;
                }
                _draftFriendIdActive = null;
                _current = vm.ConversationId;
                ActiveConversation = _current;
                HideProfile();
                ChatTitle.Text = vm.Title;
                // Re-enable the composer — a previously open CLEAR chat may
                // have left it disabled.
                MsgInput.IsEnabled = true;
                SendBtn.IsEnabled = true;
                DelChatBtn.Visibility = Visibility.Visible;
                var conv = App.Service.Conversations.FirstOrDefault(c => c.ConversationId == _current);
                if (conv != null && conv.Unread > 0)
                {
                    conv.Unread = 0;           // instant UI flip, pre-REST
                    RefreshConvList();
                    _ = App.Service.ReadAsync(_current);
                }
                await App.Service.FetchMessagesAsync(_current);
                ReloadMessages(_current);
            }
        }

        // ---------------- tabs / friends (mirrors the website panel) ----------------
        public ObservableCollection<ConvVm> Friends => _friends;
        // DataContext binding surface for the two list panes.
        public System.Collections.ObjectModel.ObservableCollection<ConvVm> Conversations => _convs;

        private void TabChats_Click(object sender, RoutedEventArgs e) => ShowTab(false);
        private void TabFriends_Click(object sender, RoutedEventArgs e) => ShowTab(true);

        private void ShowTab(bool friends)
        {
            HideProfile();
            _onFriendsTab = friends;
            ChatsPane.Visibility = friends ? Visibility.Collapsed : Visibility.Visible;
            FriendsPane.Visibility = friends ? Visibility.Visible : Visibility.Collapsed;
            TabChatsBtn.Foreground = friends ? ((Brush)FindResource("MutedBrush")) : ((Brush)FindResource("TextBrush"));
            TabFriendsBtn.Foreground = friends ? ((Brush)FindResource("TextBrush")) : ((Brush)FindResource("MutedBrush"));
            if (friends) { RefreshFriendList(); if (FriendList.ItemsSource == null) FriendList.ItemsSource = _friends; }
            else RefreshConvList();
        }

        private void RefreshFriendList()
        {
            var svc = App.Service;
            if (svc == null) return;
            var snapshot = svc.FriendIds.ToList();
            _friends.Clear();
            foreach (var id in snapshot)
            {
                // Prefer the rich profile (banner/about) for the Friends tab;
                // fall back to the light Profiles map.
                string username, picture, banner = "", about = "";
                if (svc.FriendProfiles.TryGetValue(id, out var rich))
                {
                    username = rich.username; picture = rich.picture; banner = rich.banner; about = rich.about;
                }
                else if (svc.Profiles.TryGetValue(id, out var prof))
                {
                    username = prof.username; picture = prof.picture;
                }
                else { username = ""; picture = ""; }
                var name = string.IsNullOrEmpty(username)
                    ? (id.Length > 8 ? id.Substring(0, 8) : id)
                    : username;
                // Show the ongoing chat's last message as the row preview —
                // friends with no conversation yet read "say hi!".
                var conv = svc.Conversations.FirstOrDefault(c => c.ConversationId != null && c.OtherId == id);
                string preview = conv != null && !string.IsNullOrEmpty(conv.LastBody) ? conv.LastBody : "say hi!";

                _friends.Add(new ConvVm
                {
                    ConversationId = conv?.ConversationId,
                    OtherId = id,
                    Title = name,
                    Preview = preview,
                    Initial = (string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1)).ToUpperInvariant(),
                    Picture = picture,
                    AvatarBrush = GetAvatarBrush(picture),
                    Banner = banner,
                    About = about,
                    Unread = conv?.Unread ?? 0,
                });
            }
            FriendsEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Clicking a friend card: show their profile on the right.
        private void FriendList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_onFriendsTab && FriendList.SelectedItem is ConvVm vm)
            {
                FriendList.SelectedItem = null;
                ShowProfile(vm.OtherId, vm.Title, vm.Picture, vm.Banner, vm.About);
            }
        }

        // ------------- friend profile pane (right side, like the site) -------------
        private void ShowProfile(string friendId, string name, string picture, string banner, string about)
        {
            _profileFriendId = friendId;
            ProfileBanner = banner;
            ProfilePicture = picture;
            ProfileInitial = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            ProfName.Text = name;
            ProfAbout.Text = string.IsNullOrEmpty(about) ? "No about yet." : about;
            ProfAbout.Visibility = Visibility.Visible;
            // Message button reads "Open chat" when a thread already exists.
            var existing = App.Service.Conversations.FirstOrDefault(c => c.OtherId == friendId);
            ProfMessageBtn.Content = existing != null ? "Open chat" : "Message";
            ProfilePane.Visibility = Visibility.Visible;
        }

        private void HideProfile()
        {
            ProfilePane.Visibility = Visibility.Collapsed;
            _profileFriendId = null;
        }

        // Message/Open chat from the profile pane: opens a THREAD in the
        // Chats section. For a friend with no messages yet this creates a
        // draft thread row in Chats — the server conversation is only created
        // when the first message is actually sent (matches the website).
        private async void ProfMessage_Click(object sender, RoutedEventArgs e)
        {
            var friendId = _profileFriendId;
            if (string.IsNullOrEmpty(friendId)) return;
            HideProfile();
            await OpenThreadWithFriendAsync(friendId);
        }

        /// <summary>
        /// Opens the thread with a friend on the Chats tab. If the friend has
        /// no conversation with messages yet, shows a draft thread (their row
        /// in the Chats list, empty message area, composer ready). The real
        /// conversation materializes on the first send.
        /// </summary>
        private async System.Threading.Tasks.Task OpenThreadWithFriendAsync(string friendId)
        {
            App.Service.Profiles.TryGetValue(friendId, out var prof);
            var name = string.IsNullOrEmpty(prof.username)
                ? (friendId.Length > 8 ? friendId.Substring(0, 8) : friendId)
                : prof.username;
            var existing = App.Service.Conversations.FirstOrDefault(c => c.OtherId == friendId && !string.IsNullOrEmpty(c.LastAt));

            ShowTab(false); // swap to the Chats side
            DelConfirmPanel.Visibility = Visibility.Collapsed;

            if (existing != null)
            {
                var vm = _convs.FirstOrDefault(c => c.ConversationId == existing.ConversationId);
                if (vm != null) { ConvList.SelectedItem = vm; return; }
                // Conv exists server-side but was filtered (only cleared?
                // Chats keeps cleared threads, so fall through to open it).
                _current = existing.ConversationId;
                ActiveConversation = _current;
                ChatTitle.Text = name;
                MsgInput.IsEnabled = true; SendBtn.IsEnabled = true;
                DelChatBtn.Visibility = Visibility.Visible;
                await App.Service.FetchMessagesAsync(_current);
                ReloadMessages(_current);
                return;
            }

            // ---- DRAFT THREAD: no messages yet with this friend ----
            _current = null;
            _draftBackingConvId = null;
            _draftFriendIdActive = friendId;
            ActiveConversation = null;
            ChatTitle.Text = name;
            _msgs.Clear();
            ClearedText.Visibility = Visibility.Collapsed;
            DelChatBtn.Visibility = Visibility.Collapsed;
            MsgInput.IsEnabled = true;
            SendBtn.IsEnabled = true;
            _lastRenderedSig = "draft:0";

            // Materialize a visible draft row in the Chats list.
            if (!_convs.Any(c => c.ConversationId == null && c.OtherId == friendId))
            {
                _convs.Insert(0, new ConvVm
                {
                    ConversationId = null,
                    OtherId = friendId,
                    Title = name,
                    Preview = "New chat — say hi!",
                    Initial = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(),
                    Picture = prof.picture,
                    AvatarBrush = GetAvatarBrush(prof.picture),
                    Unread = 0,
                });
            }
        }

        // Draft thread state: the friend we're composing to + the server conv
        // id once the first message created it mid-thread.
        private string _draftFriendIdActive;
        private string _draftBackingConvId;

        // ---------------- messages ----------------
        public sealed class MsgVm
        {
            public string Body { get; set; }
            public string Stamp { get; set; }
            public HorizontalAlignment Align { get; set; }
            public Brush BubbleBrush { get; set; }
            public Brush BubbleBorder { get; set; }
        }

        private void ReloadMessages(string convId)
        {
            var svc = App.Service;
            var list = svc.GetCachedMessages(convId);
            _msgs.Clear();
            var me = App.UserId;
            foreach (var m in list.OrderBy(x => x.CreatedAt, StringComparer.Ordinal))
            {
                bool mine = m.SenderId == me;
                _msgs.Add(new MsgVm
                {
                    Body = m.Body,
                    Stamp = ParseLocal(m.CreatedAt),
                    Align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    BubbleBrush = new SolidColorBrush(mine ? Color.FromRgb(0x0D, 0x33, 0x1F) : Color.FromRgb(0x12, 0x18, 0x14)),
                    BubbleBorder = new SolidColorBrush(mine ? Color.FromRgb(0x00, 0xE6, 0x76) : Color.FromRgb(0x24, 0x40, 0x2E)),
                });
            }
            var ordered = list.OrderBy(x => x.CreatedAt, StringComparer.Ordinal).ToList();
            _lastRenderedSig = ordered.Count + ":" + (ordered.Count > 0 ? ordered[ordered.Count - 1].Id : "");
            // Cleared-state banner: any conversation whose cache is empty gets
            // a notice — "cleared from another device" when the peer or this
            // account elsewhere wiped it, plain "no messages yet" for a brand
            // new chat. Sending still works either way.
            bool cleared = ordered.Count == 0 && svc.Conversations.Any(c => c.ConversationId == convId);
            ClearedText.Text = cleared
                ? "Chat cleared — messages were removed. You can still send new ones."
                : "";
            ClearedText.Visibility = cleared ? Visibility.Visible : Visibility.Collapsed;
            LoadOlderBtn.Visibility = list.Count >= 50 ? Visibility.Visible : Visibility.Collapsed;
            MsgScroll.ScrollToEnd();
        }

        private static string ParseLocal(string iso)
        {
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                return d.ToLocalTime().ToString("HH:mm");
            return "";
        }

        private async void LoadOlder_Click(object sender, MouseButtonEventArgs e)
        {
            if (_current == null) return;
            var list = App.Service.GetCachedMessages(_current);
            if (list.Count == 0) return;
            var oldest = list.OrderBy(x => x.CreatedAt, StringComparer.Ordinal).First();
            await App.Service.FetchMessagesAsync(_current, before: oldest.CreatedAt);
            ReloadMessages(_current);
        }

        // ---------------- send ----------------
        private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendAsync();
        private async void MsgInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); }
        }

        private async System.Threading.Tasks.Task SendAsync()
        {
            var text = (MsgInput.Text ?? "").Trim();
            // Draft thread: _current is null but a friend is being composed to.
            if (text.Length == 0 || (_current == null && string.IsNullOrEmpty(_draftFriendIdActive))) return;
            MsgInput.Text = "";
            try
            {
                if (_current == null)
                {
                    // First message of a draft thread creates the conversation.
                    var convId = await App.Service.EnsureConversationAsync(_draftFriendIdActive);
                    await App.Service.BootstrapAsync();
                    _draftBackingConvId = convId;
                    _current = convId;
                    ActiveConversation = convId;
                    DelChatBtn.Visibility = Visibility.Visible;

                    await App.Service.SendAsync(convId, text);

                    // The draft row becomes the REAL row: the thread STAYS in
                    // Chats now that it has messages. RefreshConvList filters
                    // by LastAt, and the fresh bootstrap provides it.
                    RefreshConvList();
                    var real = _convs.FirstOrDefault(c => c.ConversationId == convId);
                    if (real != null) ConvList.SelectedItem = real;
                    _draftFriendIdActive = null;
                    _lastRenderedSig = null;
                    ReloadMessages(convId);
                    ErrText.Visibility = Visibility.Collapsed;
                    return;
                }

                await App.Service.SendAsync(_current, text);
                ReloadMessages(_current);
                RefreshConvList();
                ErrText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MsgInput.Text = text; // keep the draft
                ErrText.Text = "Not sent (" + ex.Message + ")";
                ErrText.Visibility = Visibility.Visible;
            }
        }

        // ---------------- delete chat (the launcher's clear) ----------------
        private void DelChat_Click(object sender, MouseButtonEventArgs e)
        {
            if (_current == null) return;
            var conv = App.Service.Conversations.FirstOrDefault(c => c.ConversationId == _current);
            var name = conv != null && !string.IsNullOrEmpty(conv.OtherUsername) ? conv.OtherUsername : ChatTitle.Text;
            DelConfirmText.Text = "Delete your chat with " + name + "?";
            DelConfirmPanel.Visibility = Visibility.Visible;
        }

        private void DelNo_Click(object sender, RoutedEventArgs e)
            => DelConfirmPanel.Visibility = Visibility.Collapsed;

        private async void DelYes_Click(object sender, RoutedEventArgs e)
        {
            var convId = _current;
            DelConfirmPanel.Visibility = Visibility.Collapsed;
            if (convId == null) return;
            try
            {
                await App.Service.ClearConversationAsync(convId);
                ErrText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ErrText.Text = "Delete failed (" + ex.Message + ")";
                ErrText.Visibility = Visibility.Visible;
            }
        }

    }
}
