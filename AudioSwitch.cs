using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Timer = System.Windows.Forms.Timer;

namespace AudioSwitch
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new Mutex(true, "AudioSwitchTray", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayContext());
            }
        }
    }

    class HotKeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        public const int Push = 1, Pull = 2;
        public event Action<int> Pressed;
        public bool Registered;

        public HotKeyWindow(uint mod, uint vk)
        {
            CreateHandle(new CreateParams());
            Rebind(mod, vk);
        }

        public static uint PullMod(uint mod) { return mod ^ 0x0004; }

        public bool Rebind(uint mod, uint vk)
        {
            UnregisterHotKey(Handle, Push);
            UnregisterHotKey(Handle, Pull);
            Registered = RegisterHotKey(Handle, Push, mod | 0x4000, vk);
            if (Registered) RegisterHotKey(Handle, Pull, PullMod(mod) | 0x4000, vk);
            return Registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, Push);
            UnregisterHotKey(Handle, Pull);
            DestroyHandle();
        }
    }

    // Client for the Audio Over IP sender (github.com/ElTheLedge/Audio-Over-IP):
    // GET /getAudioConfig for the format, then interleaved float32 PCM frames over the /audio websocket.
    class Receiver
    {
        public volatile bool Feed;
        public volatile string Status = "starting";
        public long Bytes;

        readonly string host;
        readonly int port;
        volatile bool stop;
        ClientWebSocket ws;
        readonly ManualResetEvent wake = new ManualResetEvent(false);

        public Receiver(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public bool Connected { get { return Status.StartsWith("connected"); } }

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            stop = true;
            wake.Set();
            var w = ws;
            if (w != null) { try { w.Abort(); } catch { } }
        }

        void Loop()
        {
            while (!stop)
            {
                WasapiOut output = null;
                try
                {
                    Status = "connecting";
                    string baseHost = host + ":" + port;
                    string json;
                    using (var wc = new WebClient())
                        json = wc.DownloadString("http://" + baseHost + "/getAudioConfig?deviceID=default");
                    var cfg = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                    int rate = Convert.ToInt32(cfg["SamplesPerSec"]);
                    int channels = Convert.ToInt32(cfg["Channels"]);
                    var fmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

                    var buf = new BufferedWaveProvider(fmt);
                    buf.BufferDuration = TimeSpan.FromMilliseconds(500);
                    buf.DiscardOnBufferOverflow = true;
                    output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
                    output.Init(buf);
                    output.Play();

                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                    using (var cts = new CancellationTokenSource(5000))
                        ws.ConnectAsync(new Uri("ws://" + baseHost + "/audio?deviceID=default"), cts.Token).Wait();

                    Status = "connected " + rate + " Hz " + channels + " ch";
                    int maxLag = fmt.AverageBytesPerSecond * 150 / 1000;
                    var rx = new byte[1 << 16];
                    var msg = new MemoryStream();

                    while (!stop && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult r;
                        using (var cts = new CancellationTokenSource(60000))
                            r = ws.ReceiveAsync(new ArraySegment<byte>(rx), cts.Token).Result;
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        msg.Write(rx, 0, r.Count);
                        if (!r.EndOfMessage) continue;

                        byte[] data = msg.ToArray();
                        msg.SetLength(0);
                        Interlocked.Add(ref Bytes, data.Length);

                        if (!Feed)
                        {
                            if (buf.BufferedBytes > 0) buf.ClearBuffer();
                            continue;
                        }
                        // Drop the backlog instead of letting latency creep up.
                        if (buf.BufferedBytes > maxLag) buf.ClearBuffer();
                        buf.AddSamples(data, 0, data.Length);
                    }
                    Status = "disconnected";
                }
                catch (Exception e)
                {
                    Exception inner = e;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    var http = inner as WebException;
                    var resp = http != null ? http.Response as HttpWebResponse : null;
                    if (resp != null && (int)resp.StatusCode == 500)
                        Status = "sender error 500: peer has no active playback device?";
                    else
                        Status = "offline: " + inner.Message;
                }
                finally
                {
                    var w = ws;
                    ws = null;
                    if (w != null) { try { w.Abort(); w.Dispose(); } catch { } }
                    if (output != null) { try { output.Stop(); output.Dispose(); } catch { } }
                }
                if (!stop) wake.WaitOne(3000);
            }
        }
    }

    // One-line text protocol between the two AudioSwitch instances on TCP controlPort:
    //   SENDING 1|0  the peer started or stopped sending its audio to us. Replies "OK receiver=connected|offline".
    //   TOGGLE       the peer asks us to toggle sending our audio to it (pull). Replies "OK".
    static class PeerLink
    {
        public static string Ask(string host, int port, string line)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return null;
                    c.EndConnect(ar);
                    var s = c.GetStream();
                    s.ReadTimeout = 2000;
                    var b = Encoding.ASCII.GetBytes(line + "\n");
                    s.Write(b, 0, b.Length);
                    return new StreamReader(s, Encoding.ASCII).ReadLine();
                }
            }
            catch { return null; }
        }
    }

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc p, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        uint hkMod = 0, hkVk = 0x7C;
        string hkName = "F13";
        LowLevelProc hookProc;
        IntPtr hookHandle = IntPtr.Zero;
        Timer captureTimeout = new Timer();
        string lastCapture = "";

        NotifyIcon ni = new NotifyIcon();
        Timer timer = new Timer();
        HotKeyWindow hk;
        Control invoker = new Control();
        Icon localIcon, sendIcon, recvIcon;
        string dir = AppDomain.CurrentDomain.BaseDirectory;

        Receiver receiver;
        string configError = "";
        string peerHost;
        int peerControlPort = 8091, controlPort = 8091;
        TcpListener listener;
        HashSet<string> peerAddrs = new HashSet<string>();

        volatile bool sending;
        string mutedDeviceId;
        int heartbeatTick, peerMisses;
        volatile string peerState = "not contacted";
        long lastPeerMsgTicks;
        readonly object logLock = new object();

        string EventLog { get { return Path.Combine(dir, "events.log"); } }
        string PrefsPath { get { return Path.Combine(dir, "prefs.txt"); } }

        // Off by default; toggled under Preferences in the tray menu and saved per machine.
        bool notifications;

        void LoadPrefs()
        {
            try
            {
                if (!File.Exists(PrefsPath)) return;
                foreach (var line in File.ReadAllLines(PrefsPath))
                    if (line.Trim() == "notifications=1") notifications = true;
            }
            catch { }
        }

        void SavePrefs()
        {
            try { File.WriteAllText(PrefsPath, "notifications=" + (notifications ? "1" : "0") + Environment.NewLine); } catch { }
        }

        void Notify(int ms, string title, string text, ToolTipIcon icon)
        {
            Log("notice: " + text);
            if (notifications) ni.ShowBalloonTip(ms, title, text, icon);
        }

        void Log(string what)
        {
            lock (logLock)
            {
                try
                {
                    var f = new FileInfo(EventLog);
                    if (f.Exists && f.Length > 1024 * 1024) File.Delete(EventLog);
                    File.AppendAllText(EventLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + what + Environment.NewLine);
                }
                catch { }
            }
        }

        void SetFeed(bool on, string why)
        {
            if (receiver == null || receiver.Feed == on) return;
            receiver.Feed = on;
            Log("feed " + (on ? "ON" : "OFF") + " (" + why + ")");
        }

        string MuteFlag { get { return Path.Combine(dir, "endpoint-muted.txt"); } }
        string CfgPath { get { return Path.Combine(dir, "hotkey.txt"); } }

        public TrayContext()
        {
            invoker.CreateControl();
            localIcon = MakeIcon(Color.FromArgb(0, 120, 215), "LO");
            sendIcon = MakeIcon(Color.FromArgb(230, 120, 0), "TX");
            recvIcon = MakeIcon(Color.FromArgb(16, 137, 62), "RX");

            RestoreEndpointAfterCrash();
            LoadHotkey();
            LoadPrefs();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Send my audio to peer / play locally", null, delegate { ToggleSend(); });
            menu.Items.Add("Pull peer's audio here", null, delegate { Pull(); });
            var prefs = new ToolStripMenuItem("Preferences");
            var notifyItem = new ToolStripMenuItem("Show notifications") { CheckOnClick = true, Checked = notifications };
            notifyItem.CheckedChanged += delegate { notifications = notifyItem.Checked; SavePrefs(); };
            prefs.DropDownItems.Add(notifyItem);
            prefs.DropDownItems.Add("Set hotkey...", null, delegate { StartCapture(); });
            menu.Items.Add(prefs);
            menu.Items.Add("Exit", null, delegate { Quit(); });
            ni.ContextMenuStrip = menu;
            ni.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleSend(); };
            ni.Icon = localIcon;
            ni.Visible = true;

            hk = new HotKeyWindow(hkMod, hkVk);
            hk.Pressed += delegate(int id)
            {
                Log("hotkey " + (id == HotKeyWindow.Pull ? "PULL" : "PUSH") + " pressed");
                if (id == HotKeyWindow.Pull) Pull(); else ToggleSend();
            };

            captureTimeout.Interval = 20000;
            captureTimeout.Tick += delegate { EndCapture(); ni.ShowBalloonTip(1500, "Hotkey", "Capture timed out", ToolTipIcon.None); };

            LoadNetwork();
            UpdateUi();
            if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--capture") StartCapture();

            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        // ---- configuration ----

        void LoadNetwork()
        {
            string path = Path.Combine(dir, "local", "network.json");
            try
            {
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
                var peer = (Dictionary<string, object>)root["peer"];
                peerHost = (string)peer["host"];
                int port = peer.ContainsKey("port") ? Convert.ToInt32(peer["port"]) : 8080;
                if (peer.ContainsKey("controlPort")) peerControlPort = Convert.ToInt32(peer["controlPort"]);
                if (root.ContainsKey("controlPort")) controlPort = Convert.ToInt32(root["controlPort"]);
                ResolvePeer();

                receiver = new Receiver(peerHost, port);
                receiver.Feed = false;
                receiver.Start();
                StartControlServer();
                Log("started: peer " + peerHost + ":" + port + " control " + peerControlPort + ", listening on " + controlPort + ", feed OFF, mode LOCAL");
            }
            catch (Exception e)
            {
                configError = "config error: " + e.Message;
                Notify(4000, "AudioSwitch", "Can't read " + path, ToolTipIcon.Warning);
            }
        }

        void ResolvePeer()
        {
            var set = new HashSet<string>();
            try { foreach (var a in Dns.GetHostAddresses(peerHost)) set.Add(a.ToString()); } catch { }
            set.Add("127.0.0.1");
            set.Add("::1");
            peerAddrs = set;
        }

        // ---- control channel ----

        void StartControlServer()
        {
            listener = new TcpListener(IPAddress.Any, controlPort);
            listener.Start();
            var t = new Thread(delegate()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    try { Handle(c); } catch { }
                    finally { c.Close(); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Handle(TcpClient c)
        {
            var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (!peerAddrs.Contains(remote.ToString()))
            {
                ResolvePeer();
                if (!peerAddrs.Contains(remote.ToString())) { Log("control: rejected connection from " + remote); return; }
            }
            var s = c.GetStream();
            s.ReadTimeout = 2000;
            string line = new StreamReader(s, Encoding.ASCII).ReadLine();
            Interlocked.Exchange(ref lastPeerMsgTicks, DateTime.UtcNow.Ticks);
            string reply = "ERR";
            if (line == "SENDING 1")
            {
                // Only feed when our receiver can actually play the peer's stream.
                if (receiver.Connected)
                {
                    SetFeed(true, "peer says SENDING 1 from " + remote);
                    // Only one side sends at a time: two senders would loop each other's audio back and forth.
                    if (sending) invoker.BeginInvoke((MethodInvoker)delegate { GoLocal(false, "Peer is sending to you, so your audio plays here again"); });
                    reply = "OK receiver=connected";
                }
                else reply = "OK receiver=offline";
            }
            else if (line == "SENDING 0")
            {
                SetFeed(false, "peer says SENDING 0");
                reply = "OK";
            }
            else if (line == "TOGGLE")
            {
                Log("control: peer asked us to toggle sending (pull)");
                invoker.BeginInvoke((MethodInvoker)delegate { ToggleSend(); });
                reply = "OK";
            }
            if (line != "SENDING 0" || reply != "OK") Log("control in: '" + line + "' from " + remote + " -> " + reply);
            var b = Encoding.ASCII.GetBytes(reply + "\n");
            s.Write(b, 0, b.Length);
        }

        // ---- switching ----

        void ToggleSend()
        {
            if (sending) { GoLocal(true, null); return; }
            if (receiver == null) { Notify(3000, "AudioSwitch", configError, ToolTipIcon.Warning); return; }

            string reply = PeerLink.Ask(peerHost, peerControlPort, "SENDING 1");
            Log("control out: 'SENDING 1' -> " + (reply ?? "no answer"));
            if (reply == null || !reply.StartsWith("OK"))
            {
                peerState = "unreachable";
                // The peer may have acted on it even if the answer got lost, so take it back.
                ThreadPool.QueueUserWorkItem(delegate { PeerLink.Ask(peerHost, peerControlPort, "SENDING 0"); });
                Notify(3000, "AudioSwitch", "Peer AudioSwitch isn't answering. Your audio stays here.", ToolTipIcon.Warning);
                return;
            }
            if (!reply.Contains("receiver=connected"))
            {
                PeerLink.Ask(peerHost, peerControlPort, "SENDING 0");
                peerState = "reachable, not receiving";
                Notify(3000, "AudioSwitch", "Peer isn't connected to your sender. Your audio stays here.", ToolTipIcon.Warning);
                return;
            }
            peerState = "ok";
            peerMisses = 0;
            SetFeed(false, "we started sending");
            sending = true;
            Log("mode SENDING (endpoint muted)");
            MuteEndpoint(true);
            Notify(1000, "Audio", "Sending your audio to the peer", ToolTipIcon.None);
            UpdateUi();
        }

        void GoLocal(bool notifyPeer, string why)
        {
            bool was = sending;
            sending = false;
            if (was) Log("mode LOCAL (" + (why ?? "toggled back") + ")");
            MuteEndpoint(false);
            if (notifyPeer && peerHost != null)
                ThreadPool.QueueUserWorkItem(delegate { PeerLink.Ask(peerHost, peerControlPort, "SENDING 0"); });
            if (was) Notify(1500, "Audio", why ?? "Playing your audio here", ToolTipIcon.None);
            UpdateUi();
        }

        void Pull()
        {
            if (peerHost == null) return;
            if (sending) GoLocal(true, null);
            string reply = PeerLink.Ask(peerHost, peerControlPort, "TOGGLE");
            Log("control out: 'TOGGLE' -> " + (reply ?? "no answer"));
            Notify(2000, "Audio", reply == null ? "Peer AudioSwitch isn't answering" : "Asked the peer to toggle sending", ToolTipIcon.None);
        }

        // Sending mutes this machine's output endpoint. Loopback capture is taken before endpoint
        // mute, so the peer still gets full-level audio. App-session mute would silence the capture too.
        void MuteEndpoint(bool mute)
        {
            try
            {
                var en = new MMDeviceEnumerator();
                if (mute)
                {
                    var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    if (!dev.AudioEndpointVolume.Mute)
                    {
                        dev.AudioEndpointVolume.Mute = true;
                        mutedDeviceId = dev.ID;
                        File.WriteAllText(MuteFlag, dev.ID);
                    }
                }
                else if (mutedDeviceId != null)
                {
                    en.GetDevice(mutedDeviceId).AudioEndpointVolume.Mute = false;
                    mutedDeviceId = null;
                    File.Delete(MuteFlag);
                }
            }
            catch { }
        }

        void RestoreEndpointAfterCrash()
        {
            try
            {
                if (!File.Exists(MuteFlag)) return;
                new MMDeviceEnumerator().GetDevice(File.ReadAllText(MuteFlag).Trim()).AudioEndpointVolume.Mute = false;
                File.Delete(MuteFlag);
            }
            catch { }
        }

        void Tick()
        {
            // Every 5 s tell the peer our state, so a missed message or a restart can't leave it out of sync.
            if (receiver != null && ++heartbeatTick >= 5)
            {
                heartbeatTick = 0;
                bool s = sending;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string reply = PeerLink.Ask(peerHost, peerControlPort, s ? "SENDING 1" : "SENDING 0");
                    bool lost = reply == null || (s && !reply.Contains("receiver=connected"));
                    peerState = reply == null ? "unreachable" : (lost ? "not receiving" : "ok");
                    if (s && lost && ++peerMisses >= 3)
                        invoker.BeginInvoke((MethodInvoker)delegate { if (sending) GoLocal(false, "Lost the peer, so your audio plays here again"); });
                    if (!lost) peerMisses = 0;
                });
            }
            // The peer heartbeats every 5 s. If it goes quiet while we play its stream, stop playing it.
            if (receiver != null && receiver.Feed &&
                DateTime.UtcNow.Ticks - Interlocked.Read(ref lastPeerMsgTicks) > TimeSpan.FromSeconds(15).Ticks)
                SetFeed(false, "no word from peer for 15 s");
            UpdateUi();
        }

        // ---- UI and state ----

        string ReceiverStatus { get { return receiver != null ? receiver.Status : configError; } }

        string Mode
        {
            get
            {
                if (sending) return "SENDING";
                return receiver != null && receiver.Feed ? "RECEIVING" : "LOCAL";
            }
        }

        void UpdateUi()
        {
            string mode = Mode;
            ni.Icon = mode == "SENDING" ? sendIcon : mode == "RECEIVING" ? recvIcon : localIcon;
            string t = mode + " | " + hkName + " | peer stream: " + ReceiverStatus;
            ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            WriteState();
        }

        void WriteState()
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "state.txt"),
                    "mode=" + Mode + " feed=" + (receiver != null && receiver.Feed ? "1" : "0") +
                    " hotkey=" + hkName + (hk.Registered ? " ok" : " FAILED") + " pull=" + NameOf(HotKeyWindow.PullMod(hkMod), hkVk) +
                    " receiver=[" + ReceiverStatus + "] bytes=" + (receiver != null ? Interlocked.Read(ref receiver.Bytes) : 0) +
                    " peer-control=[" + peerState + "] endpoint-muted=" + (mutedDeviceId != null ? "yes" : "no") +
                    " time=" + DateTime.Now.ToString("s"));
            }
            catch { }
        }

        static string NameOf(uint mod, uint vk)
        {
            string s = "";
            if ((mod & 2) != 0) s += "Ctrl+";
            if ((mod & 1) != 0) s += "Alt+";
            if ((mod & 4) != 0) s += "Shift+";
            if ((mod & 8) != 0) s += "Win+";
            return s + ((Keys)vk).ToString();
        }

        void LoadHotkey()
        {
            try
            {
                if (!File.Exists(CfgPath)) return;
                var parts = File.ReadAllText(CfgPath).Trim().Split(',');
                hkMod = uint.Parse(parts[0]);
                hkVk = uint.Parse(parts[1]);
                hkName = NameOf(hkMod, hkVk);
            }
            catch { }
        }

        void StartCapture()
        {
            if (hookHandle != IntPtr.Zero) return;
            hookProc = delegate(int nCode, IntPtr wParam, IntPtr lParam)
            {
                int msg = (int)wParam;
                if (nCode >= 0 && (msg == 0x100 || msg == 0x104))
                {
                    uint vk = (uint)Marshal.ReadInt32(lParam);
                    uint scan = (uint)Marshal.ReadInt32(lParam, 4);
                    bool isMod = (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
                    if (!isMod)
                    {
                        uint mod = 0;
                        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mod |= 1;
                        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mod |= 2;
                        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mod |= 4;
                        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) mod |= 8;
                        lastCapture = "vk=0x" + vk.ToString("X") + " scan=0x" + scan.ToString("X") + " mod=" + mod;
                        ApplyCapture(vk, mod);
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            };
            hookHandle = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
            captureTimeout.Start();
            ni.ShowBalloonTip(3000, "Set hotkey", "Press the key you want now (20 s). Shift plus it becomes pull.", ToolTipIcon.None);
        }

        void ApplyCapture(uint vk, uint mod)
        {
            Timer t = new Timer();
            t.Interval = 10;
            t.Tick += delegate
            {
                t.Stop();
                EndCapture();
                if (vk == 0x1B && mod == 0) { ni.ShowBalloonTip(1500, "Hotkey", "Cancelled", ToolTipIcon.None); return; }
                if (hk.Rebind(mod, vk))
                {
                    hkMod = mod; hkVk = vk; hkName = NameOf(mod, vk);
                    try { File.WriteAllText(CfgPath, mod + "," + vk); } catch { }
                    ni.ShowBalloonTip(2000, "Hotkey set", hkName + " (pull: " + NameOf(HotKeyWindow.PullMod(mod), vk) + ")", ToolTipIcon.None);
                }
                else
                {
                    hk.Rebind(hkMod, hkVk);
                    ni.ShowBalloonTip(2500, "Hotkey failed", hkName + " unchanged. That key can't be registered.", ToolTipIcon.None);
                }
                UpdateUi();
            };
            t.Start();
        }

        void EndCapture()
        {
            captureTimeout.Stop();
            if (hookHandle != IntPtr.Zero) { UnhookWindowsHookEx(hookHandle); hookHandle = IntPtr.Zero; }
        }

        static Icon MakeIcon(Color c, string text)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 0, 0, 31, 31);
                using (var f = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var sz = g.MeasureString(text, f);
                    g.DrawString(text, f, Brushes.White, (32 - sz.Width) / 2, (32 - sz.Height) / 2);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        void Quit()
        {
            timer.Stop();
            EndCapture();
            if (sending)
            {
                sending = false;
                MuteEndpoint(false);
                if (peerHost != null) PeerLink.Ask(peerHost, peerControlPort, "SENDING 0");
            }
            if (receiver != null) receiver.Stop();
            if (listener != null) { try { listener.Stop(); } catch { } }
            hk.Dispose();
            ni.Visible = false;
            ExitThread();
        }
    }
}
