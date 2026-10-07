using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeil.Messages
{
    /// <summary>
    /// Shared messaging backend client (launcher side). Talks to the same
    /// messaging-api Edge Function and Supabase Realtime as the website:
    /// one backend, same conversations, same messages, same friends table.
    /// Live delivery = Realtime broadcast (ids only) + authed fetch. No polling.
    /// </summary>
    public class MessagingService : IDisposable
    {
        private const string SupabaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co";
        private const string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImxxZ2h1cnZvbnJ2cnhmd2pna3V1Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzEyODA1OTMsImV4cCI6MjA4Njg1NjU5M30.TUulKqgdUdoaYn9O5SKOJfh61DLprLTvf2fU_9CNF_U";
        private static readonly string FnBase = SupabaseUrl + "/functions/v1";

        private readonly string _token;
        private readonly HttpClient _http;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _wsLoop;
        private readonly ConcurrentQueue<int> _refQueue = new ConcurrentQueue<int>();
        private int _ref;
        private int _heartbeats;
        private DateTime _lastHeartbeatAck = DateTime.UtcNow;
        private readonly object _wsLock = new object();
        private readonly SemaphoreSlim _wsSendLock = new(1, 1);
        private readonly HashSet<string> _joinedTopics = new HashSet<string>();
        private readonly object _pingGate = new object();
        private readonly HashSet<string> _pingsInFlight = new HashSet<string>();
        // Messages whose unread/ping side-effects were already applied
        // (ping path or reconcile path — whichever got there first).
        private readonly HashSet<string> _countedUnreadIds = new HashSet<string>();

        public bool SoundEnabled { get; set; }
            = !string.Equals(Environment.GetEnvironmentVariable("LV_MSG_SOUND"), "off", StringComparison.OrdinalIgnoreCase);

        public string ConnState { get; private set; } = "wait";
        public event Action<string> StateChanged;
        public event Action DataChanged;          // bootstrap-level change (conversations/unread)
        public event Action<string> MessageArrived; // conversation_id
        public event Action<string, string> ConversationCleared; // (conversation_id, cleared by)
        public event Action<string> ErrorOccurred;

        public class Conversation
        {
            public string ConversationId;
            public string OtherId;
            public string OtherUsername;
            public string OtherPicture;
            public int Unread;
            public string LastBody;
            public string LastAt;
        }

        public class Msg
        {
            public string Id;
            public string ConversationId;
            public string SenderId;
            public string Body;
            public string CreatedAt;
        }

        public List<Conversation> Conversations { get; } = new List<Conversation>();
        public HashSet<string> FriendIds { get; } = new HashSet<string>();
        public Dictionary<string, (string username, string picture)> Profiles { get; }
            = new Dictionary<string, (string, string)>();
        public HashSet<string> SeenMessageIds { get; } = new HashSet<string>();

        private readonly Dictionary<string, List<Msg>> _cache = new Dictionary<string, List<Msg>>();

        public MessagingService(string launcherToken)
        {
            _token = launcherToken ?? "";
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_token))
            {
                ConnState = "off";
                StateChanged?.Invoke(ConnState);
                return;
            }
            _wsLoop = Task.Run(() => WebsocketLoop(_cts.Token));
            _keepFreshLoop = Task.Run(() => KeepFreshLoop(_cts.Token));
        }

        private Task _keepFreshLoop;

        /// <summary>
        /// Keeps the session "loaded": periodic REST reconcile as a realtime
        /// fallback, periodic re-bootstrap so newly-created conversations
        /// appear without a restart, and channel re-joins for conversations
        /// discovered after the socket connected.
        /// </summary>
        private async Task KeepFreshLoop(CancellationToken ct)
        {
            var cycle = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
                catch (TaskCanceledException) { break; }
                try
                {
                    cycle++;
                    lock (this)
                    {
                        foreach (var c in Conversations) EnsureChannelJoined(c.ConversationId);
                    }
                    if (cycle % 3 == 0) await BootstrapAsync();
                    else await ReconcileAsync();
                }
                catch { /* next cycle retries */ }
            }
        }

        private async Task<JsonElement> PostAsync(string action, object body)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object> { ["action"] = action });
            if (body != null)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(body));
                var merged = new Dictionary<string, object> { ["action"] = action };
                foreach (var kv in dict) merged[kv.Key] = JsonSerializer.Deserialize<JsonElement>(kv.Value.GetRawText());
                payload = JsonSerializer.Serialize(merged);
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, FnBase + "/messaging-api");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            req.Headers.Add("apikey", AnonKey);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("messaging-api " + action + ": " + resp.StatusCode + " " + text);
            return JsonSerializer.Deserialize<JsonElement>(text);
        }

        public async Task BootstrapAsync()
        {
            var el = await PostAsync("bootstrap", null);
            lock (this)
            {
                Conversations.Clear();
                FriendIds.Clear();
                Profiles.Clear();

                foreach (var f in el.GetProperty("friends").EnumerateArray())
                {
                    var id = f.GetProperty("productUserId").GetString();
                    FriendIds.Add(id);
                    Profiles[id] = (f.GetProperty("username").GetString() ?? "", f.GetProperty("pictureUrl").GetString() ?? "");
                }

                foreach (var c in el.GetProperty("conversations").EnumerateArray())
                {
                    var other = c.GetProperty("other");
                    var conv = new Conversation
                    {
                        ConversationId = c.GetProperty("conversation_id").GetString(),
                        OtherId = c.GetProperty("other_id").GetString(),
                        Unread = c.GetProperty("unread").GetInt32(),
                        LastBody = c.TryGetProperty("last_body", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() : null,
                        LastAt = c.TryGetProperty("last_at", out var la) && la.ValueKind == JsonValueKind.String ? la.GetString() : null,
                    };
                    conv.OtherUsername = other.GetProperty("username").GetString() ?? "";
                    conv.OtherPicture = other.GetProperty("pictureUrl").GetString() ?? "";
                    Profiles[conv.OtherId] = (conv.OtherUsername, conv.OtherPicture);
                    Conversations.Add(conv);
                }
            }
            DataChanged?.Invoke();

            // Prefetch on launch: join every conversation's realtime channel
            // (a friend may have opened a conversation while we were offline)
            // and warm each conversation's latest messages so opening a chat
            // needs no fetch at all.
            try
            {
                foreach (var c in Conversations.ToList())
                    EnsureChannelJoined(c.ConversationId);
                _ = Task.Run(() => PrefetchAllAsync());
            }
            catch { /* keep-fresh loop retries */ }
        }

        /// <summary>
        /// Warms the message cache for every conversation with its most
        /// recent messages (2 concurrent fetches) so the chat view is instant.
        /// </summary>
        public async Task PrefetchAllAsync(int perConversation = 20)
        {
            var convs = Conversations.ToList();
            using var gate = new SemaphoreSlim(2, 2);
            var tasks = convs.Select(async c =>
            {
                await gate.WaitAsync();
                try { await FetchMessagesAsync(c.ConversationId, limit: perConversation); }
                catch { /* individual conversation failures are non-fatal */ }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            DataChanged?.Invoke();
        }

        public List<Msg> GetCachedMessages(string conversationId)
        {
            lock (_cache)
            {
                return _cache.TryGetValue(conversationId, out var list)
                    ? list.OrderBy(m => m.CreatedAt, StringComparer.Ordinal).ToList()
                    : new List<Msg>();
            }
        }

        public async Task<List<Msg>> FetchMessagesAsync(string conversationId, string before = null, string after = null, int limit = 50)
        {
            var el = await PostAsync("list", new { conversation_id = conversationId, before, after, limit });
            var list = new List<Msg>();
            foreach (var m in el.GetProperty("messages").EnumerateArray())
            {
                var msg = new Msg
                {
                    Id = m.GetProperty("id").GetString(),
                    ConversationId = m.GetProperty("conversation_id").GetString(),
                    SenderId = m.GetProperty("sender_id").GetString(),
                    Body = m.GetProperty("body").GetString(),
                    CreatedAt = m.GetProperty("created_at").GetString(),
                };
                list.Add(msg);
                SeenMessageIds.Add(msg.Id);
            }
            lock (_cache)
            {
                _cache[conversationId] = list
                    .Concat(_cache.TryGetValue(conversationId, out var prev) ? prev : new List<Msg>())
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .ToList();
            }
            return list;
        }

        public async Task<Msg> SendAsync(string conversationId, string body)
        {
            var el = await PostAsync("send", new { conversation_id = conversationId, body });
            var m = el.GetProperty("message");
            var msg = new Msg
            {
                Id = m.GetProperty("id").GetString(),
                ConversationId = m.GetProperty("conversation_id").GetString(),
                SenderId = m.GetProperty("sender_id").GetString(),
                Body = m.GetProperty("body").GetString(),
                CreatedAt = m.GetProperty("created_at").GetString(),
            };
            SeenMessageIds.Add(msg.Id);
            lock (_cache)
            {
                if (!_cache.TryGetValue(conversationId, out var list)) _cache[conversationId] = list = new List<Msg>();
                if (!list.Any(x => x.Id == msg.Id)) list.Add(msg);
            }
            var conv = Conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            if (conv != null) { conv.LastBody = msg.Body; conv.LastAt = msg.CreatedAt; }
            DataChanged?.Invoke();
            return msg;
        }

        public async Task ReadAsync(string conversationId)
        {
            var conv = Conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            if (conv != null) conv.Unread = 0;
            DataChanged?.Invoke();
            await PostAsync("read", new { conversation_id = conversationId });
        }

        public async Task<string> EnsureConversationAsync(string friendId)
        {
            var el = await PostAsync("ensure", new { friend_id = friendId });
            return el.GetProperty("conversation_id").GetString();
        }

        /// <summary>
        /// clears ALL messages in a conversation (server does the same for the
        /// peer) and resets local state. Same semantics as the website's
        /// "delete conversation": the conversation itself stays, the peer is
        /// told via the conversation-cleared broadcast.
        /// </summary>
        public async Task<int> ClearConversationAsync(string conversationId)
        {
            var el = await PostAsync("clear", new { conversation_id = conversationId });
            int deleted = el.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            OnConversationCleared(conversationId, App.UserId);
            return deleted;
        }

        // ------------------------------------------------------------------
        // Realtime: one WebSocket, one broadcast channel per conversation.
        // Payload carries ids only; bodies are fetched via authed API (dedupe).
        // ------------------------------------------------------------------
        private async Task WebsocketLoop(CancellationToken ct)
        {
            var backoff = TimeSpan.FromSeconds(2);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ConnState = "wait"; StateChanged?.Invoke(ConnState);
                    using var ws = new ClientWebSocket();
                    lock (_wsLock) _ws = ws;
                    var uri = new Uri(SupabaseUrl.Replace("https://", "wss://") + "/realtime/v1/websocket?apikey=" + AnonKey + "&vsn=1.0.0");
                    await ws.ConnectAsync(uri, ct);

                    _heartbeats = 0;
                    await SendJson(ws, new { topic = "phoenix", @event = "phx_join", @ref = "0", payload = new { } });

                    // Socket is up — report live BEFORE channel joins. Join
                    // hiccups must never leave the UI stuck on "connecting…"
                    // while messaging is actually working (the sweep covers
                    // any missed joins).
                    ConnState = "on"; StateChanged?.Invoke(ConnState);

                    // Re-join everything we know about on (re)connect.
                    lock (_joinedTopics) _joinedTopics.Clear();
                    try
                    {
                        // The SERVER broadcasts every new-message ping on the
                        // global channel (same one the website joins) — join
                        // it FIRST so delivery never depends on bootstrapped
                        // conversation state.
                        await JoinChannel(ws, "realtime:veilnet-msgs-live");
                        lock (_joinedTopics) _joinedTopics.Add("realtime:veilnet-msgs-live");

                        List<string> toJoin;
                        lock (this) toJoin = Conversations.Select(c => c.ConversationId).ToList();
                        foreach (var convId in toJoin)
                        {
                            try { await JoinChannel(ws, convId); }
                            catch { /* sweep covers missed joins */ }
                        }
                        lock (_joinedTopics)
                            foreach (var convId in toJoin) _joinedTopics.Add("veilnet-dm:" + convId);
                    }
                    catch { /* keep-fresh loop retries */ }
                    backoff = TimeSpan.FromSeconds(2);
                    _lastHeartbeatAck = DateTime.UtcNow;

                    var buf = new byte[256 * 1024];
                    var heartbeatTimer = Task.Run(async () =>
                    {
                        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(15), ct);
                            _heartbeats++;
                            await SendJson(ws, new { topic = "phoenix", @event = "heartbeat", @ref = "hb" + _heartbeats, payload = new { } });
                            if (DateTime.UtcNow - _lastHeartbeatAck > TimeSpan.FromSeconds(45))
                                throw new Exception("heartbeat timeout");
                        }
                    }, ct);

                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                        HandleSocketText(text, ws);
                    }
                    heartbeatTimer = null;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Debug.WriteLine("[rt] " + ex.Message);
                    ErrorOccurred?.Invoke(ex.Message);
                }

                ConnState = "wait"; StateChanged?.Invoke(ConnState);
                try { await Task.Delay(backoff, ct); } catch (TaskCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 1.6, 30));

                try
                {
                    if (Conversations.Count == 0) await BootstrapAsync();
                    await ReconcileAsync();
                }
                catch { /* next loop retries */ }
            }
        }

        private async Task JoinChannel(ClientWebSocket ws, string conversationId)
        {
            var topic = "veilnet-dm:" + conversationId;
            await SendJson(ws, new
            {
                topic,
                @event = "phx_join",
                @ref = NextRef(),
                payload = new { config = new { broadcast = new { self = false } } },
            });
        }

        /// <summary>
        /// Registers interest in a conversation's realtime channel. Safe to
        /// call any time (before/during/after the socket is up); if the
        /// socket is not open yet, the connect loop or keep-fresh loop joins
        /// it on the next (re)connect.
        /// </summary>
        private void EnsureChannelJoined(string conversationId)
        {
            var topic = "veilnet-dm:" + conversationId;
            lock (_joinedTopics)
            {
                if (!_joinedTopics.Add(topic)) return;
            }
            ClientWebSocket ws;
            lock (_wsLock) ws = _ws;
            if (ws != null && ws.State == WebSocketState.Open)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _sendGate.WaitAsync();
                        try { await JoinChannel(ws, conversationId); }
                        finally { _sendGate.Release(); }
                    }
                    catch { lock (_joinedTopics) _joinedTopics.Remove(topic); }
                });
            }
            else
            {
                // Not connected (still connecting): retry keep-fresh rejoin.
                lock (_joinedTopics) _joinedTopics.Remove(topic);
            }
        }

        private void HandleSocketText(string text, ClientWebSocket ws)
        {
            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(text); }
            catch { return; }

            var @event = el.TryGetProperty("event", out var ev) ? ev.GetString() : null;
            var topic = el.TryGetProperty("topic", out var tp) ? tp.GetString() : null;

            if (@event == "phx_reply")
            {
                var r = el.TryGetProperty("ref", out var rf) ? rf.GetString() : null;
                if (r != null && (r.StartsWith("hb") || r == "0"))
                    _lastHeartbeatAck = DateTime.UtcNow;
            }
            else if (@event == "broadcast" && topic != null &&
                     (topic.StartsWith("veilnet-dm:") || topic == "realtime:veilnet-msgs-live"))
            {
                try
                {
                    var payload = el.GetProperty("payload");
                    if (payload.TryGetProperty("type", out var t) && t.GetString() == "new-message")
                    {
                        var convId = payload.GetProperty("conversation_id").GetString();
                        var messageId = payload.GetProperty("message_id").GetString();
                        _ = Task.Run(() => OnRemotePing(convId, messageId));
                    }
                    else if (payload.TryGetProperty("type", out var t2) && t2.GetString() == "conversation-cleared")
                    {
                        // Server fires this when either side clears the chat
                        // (messaging-api "clear"): wipe our cache instantly
                        // instead of showing ghost messages until the next
                        // bootstrap.
                        var convId = payload.TryGetProperty("conversation_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null;
                        var by = payload.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                        if (!string.IsNullOrEmpty(convId))
                            _ = Task.Run(() => OnConversationCleared(convId, by));
                    }
                }
                catch { }
            }
            else if (@event == "phx_reply" && topic != null && topic.StartsWith("veilnet-dm:"))
            {
                // join ack — fine
            }
        }

        private async Task OnRemotePing(string conversationId, string messageId)
        {
            // Both the global channel and the per-conversation channel deliver
            // the same ping; process each message id only once.
            if (string.IsNullOrEmpty(messageId)) return;
            lock (_pingGate)
            {
                if (!_pingsInFlight.Add(messageId)) return;
            }
            try
            {
                var conv = Conversations.FirstOrDefault(c => c.ConversationId == conversationId);
                if (conv == null) return;

                var list = await FetchMessagesAsync(conversationId, after: null, limit: 10);
                var msg = list.FirstOrDefault(m => m.Id == messageId);
                if (msg == null) return; // RLS would hide it; not ours

                // ALWAYS refresh the UI — even if the 20s sweep already cached
                // this message, the open chat must not sit stale ("click into
                // it again" bug).
                MessageArrived?.Invoke(conversationId);

                bool isActive = MainWindow.ActiveConversation == conversationId;
                if (msg.SenderId != App.UserId && _countedUnreadIds.Add(msg.Id))
                {
                    if (isActive && !MainWindow.IsHidden)
                    {
                        await ReadAsync(conversationId);
                    }
                    else
                    {
                        conv.Unread++;
                        DataChanged?.Invoke();
                        if (SoundEnabled) Sound.PlayPing();
                    }
                }
            }
            catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); }
            finally
            {
                lock (_pingGate) _pingsInFlight.Remove(messageId);
            }
        }

        /// <summary>
        /// Either side (or another device of ours) cleared the conversation:
        /// the server rows are already gone. Wipe the local cache, reset the
        /// conversation's preview/unread, and let the window show the empty
        /// state + a notice right where the user is looking.
        /// <param name="by">account id that cleared, or null when unknown.
        /// Includes our OWN clear echoed back from the server (e.g. done on
        /// another device), so every path ends with a consistent empty view.</param>
        /// </summary>

        private void OnConversationCleared(string conversationId, string by)
        {
            var conv = Conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            if (conv != null)
            {
                conv.Unread = 0;
                conv.LastBody = null;
                conv.LastAt = null;
            }
            lock (_cache) _cache.Remove(conversationId);
            DataChanged?.Invoke();
            // Window decides how to surface it (empty chat + notice). Fired on
            // a dispatcher-adjacent background thread; subscribers marshal.
            ConversationCleared?.Invoke(conversationId, by);
        }

        public async Task ReconcileAsync()
        {
            foreach (var conv in Conversations.ToList())
            {
                var cached = GetCachedMessages(conv.ConversationId);
                var newest = cached.LastOrDefault();
                var list = await FetchMessagesAsync(conv.ConversationId,
                    after: newest?.CreatedAt, limit: 50);
                if (list.Count > 0)
                {
                    var convRef = conv;
                    foreach (var m in list)
                    {
                        if (m.SenderId != App.UserId && _countedUnreadIds.Add(m.Id))
                        {
                            bool active = MainWindow.ActiveConversation == convRef.ConversationId && !MainWindow.IsHidden;
                            if (active) { /* will be marked read */ }
                            else { convRef.Unread++; if (SoundEnabled) Sound.PlayPing(); }
                            MessageArrived?.Invoke(convRef.ConversationId);
                        }
                    }
                    DataChanged?.Invoke();
                }
            }
        }

        private string NextRef() => (_ref++).ToString();

        // Single writer: Realtime WebSocket rejects concurrent sends.
        private static async Task SendJson(ClientWebSocket ws, object o)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // (Concurrent sends on one ClientWebSocket are illegal; joins go
        // through EnsureChannelJoined's fire-and-forget Task while the
        // connect loop may be sending heartbeats at the same instant.
        // Serialize all sends through this lock.)
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private async Task SendJsonLockedSafe(ClientWebSocket ws, object o)
        {
            await _wsSendLock.WaitAsync();
            try { await SendJson(ws, o); }
            finally { _wsSendLock.Release(); }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); _ws?.Abort(); _http?.Dispose(); } catch { }
        }
    }

    public static class Sound
    {
        private static bool _playing;

        // Soft two-note chime, synthesized in memory (no audio asset shipped).
        // Low-amplitude sines with gentle fade envelopes -> a quiet "doorbell",
        // not the ear-piercing Console.Beep square wave.
        private const int SampleRate = 22050;
        private static byte[] _chimeWav;
        // Kept alive for the process lifetime: Play() is async, so disposing the
        // player (or its stream) right after Play() can truncate playback.
        private static System.Media.SoundPlayer _player;

        public static void PlayPing()
        {
            if (_playing) return;
            _playing = true;
            try
            {
                if (_player == null)
                {
                    _chimeWav = BuildChimeWav();
                    _player = new System.Media.SoundPlayer(new MemoryStream(_chimeWav));
                }
                // Play() is async (preloads + plays on its own thread); no UI block.
                _player.Play();
            }
            catch { }
            finally
            {
                Task.Delay(600).ContinueWith(_ => _playing = false);
            }
        }

        // Two gentle notes: E5 (659 Hz) then C5 (523 Hz), each 140 ms with the
        // next note starting as the previous one decays — a warm "ding-dong"
        // doorbell rather than a sharp beep.
        private static byte[] BuildChimeWav()
        {
            const int noteMs = 140;          // spacing between note starts
            const int tailMs = 260;          // decay tail after the last note
            const int note1 = 659;           // E5
            const int note2 = 523;           // C5
            const double amp = 0.16;         // quiet by design
            const double attackSec = 0.030;  // 30 ms soft fade-in per note

            int total = (noteMs + tailMs) * SampleRate / 1000;
            var samples = new short[total];
            for (int n = 0; n < 2; n++)
            {
                int start = n * (noteMs * SampleRate / 1000);
                double freq = n == 0 ? note1 : note2;
                for (int i = start; i < total; i++)
                {
                    double t = (i - start) / (double)SampleRate;
                    double attack = Math.Min(1.0, t / attackSec);
                    double decay = Math.Exp(-3.5 * (i - start) / (double)total);
                    double env = attack * decay;
                    if (i >= 0 && i < total)
                        samples[i] += (short)(amp * env * 32767.0 * Math.Sin(2 * Math.PI * freq * t));
                }
            }

            // Global 20 ms edge fades so the clip never clicks at start/end.
            int edge = SampleRate / 50;
            for (int i = 0; i < edge; i++)
            {
                samples[i] = (short)(samples[i] * i / edge);
                samples[total - 1 - i] = (short)(samples[total - 1 - i] * i / edge);
            }

            // 16-bit mono PCM WAV header + data
            int dataBytes = total * 2;
            var ms = new MemoryStream(44 + dataBytes);
            var w = new BinaryWriter(ms);
            w.Write("RIFF".ToCharArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray());
            w.Write(16);                     // fmt chunk size
            w.Write((short)1);               // PCM
            w.Write((short)1);               // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);         // byte rate = sr * blockalign
            w.Write((short)2);               // block align (mono 16-bit)
            w.Write((short)16);              // bits per sample
            w.Write("data".ToCharArray());
            w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}
