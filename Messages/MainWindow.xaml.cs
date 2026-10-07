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
        public static string ActiveConversation;
        public static bool IsHidden => !_isOpen || Application.Current == null || !Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible);
        private static bool _isOpen;

        private readonly ObservableCollection<ConvVm> _convs = new ObservableCollection<ConvVm>();
        private readonly ObservableCollection<MsgVm> _msgs = new ObservableCollection<MsgVm>();
        private string _current;
        private bool _pinned;
        public bool IsPinned => _pinned;
        private DispatcherTimer _uiTimer;

        public MainWindow()
        {
            InitializeComponent();
            _isOpen = true;
            ConvList.ItemsSource = _convs;
            MsgList.ItemsSource = _msgs;

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
            svc.DataChanged += () => Dispatcher.BeginInvoke(RefreshConvList);
            svc.MessageArrived += convId => Dispatcher.BeginInvoke(() =>
            {
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
            _uiTimer.Tick += (s, e) => RefreshConvList();
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

        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
            public string Title { get; set; }
            public string Preview { get; set; }
            public string Initial { get; set; }
            public string Picture { get; set; }
            public Brush AvatarBrush { get; set; }
            public int Unread { get; set; }
            public Visibility UnreadVisible => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
            public string UnreadText => Unread > 99 ? "99+" : (Unread > 0 ? Unread.ToString() : "");
            public Visibility PictureVisible => string.IsNullOrEmpty(Picture) ? Visibility.Collapsed : Visibility.Visible;
            public Visibility InitialVisible => string.IsNullOrEmpty(Picture) ? Visibility.Visible : Visibility.Collapsed;
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
            var snapshot = svc.Conversations
                .OrderByDescending(c => c.LastAt ?? "")
                .ToList();
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
                    Title = name,
                    Preview = c.LastBody ?? "",
                    Initial = (string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1)).ToUpperInvariant(),
                    Picture = c.OtherPicture,
                    AvatarBrush = GetAvatarBrush(c.OtherPicture),
                    Unread = c.Unread,
                });
            }
            ConvEmpty.Visibility = _convs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ConvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConvList.SelectedItem is ConvVm vm)
            {
                _current = vm.ConversationId;
                ActiveConversation = _current;
                ChatTitle.Text = vm.Title;
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
            if (text.Length == 0 || _current == null) return;
            MsgInput.Text = "";
            try
            {
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

    }
}
