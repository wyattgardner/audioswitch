using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
        public event EventHandler Pressed;
        public bool Registered;

        public HotKeyWindow(uint mod, uint vk)
        {
            CreateHandle(new CreateParams());
            Rebind(mod, vk);
        }

        public bool Rebind(uint mod, uint vk)
        {
            UnregisterHotKey(Handle, 1);
            Registered = RegisterHotKey(Handle, 1, mod | 0x4000, vk);
            return Registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }
    }

    static class Sessions
    {
        public delegate void SessionVisitor(uint pid, SimpleAudioVolume vol);

        public static void ForEach(SessionVisitor visit)
        {
            var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var c = sessions[i];
                if (c.IsSystemSoundsSession) continue;
                visit(c.GetProcessID, c.SimpleAudioVolume);
            }
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
                        Status = "sender error 500: laptop has no active playback device?";
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

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc p, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        uint hkMod = 0x0003, hkVk = 0x78;
        string hkName = "Ctrl+Alt+F9";
        LowLevelProc hookProc;
        IntPtr hookHandle = IntPtr.Zero;
        Timer captureTimeout = new Timer();
        string lastCapture = "";

        NotifyIcon ni = new NotifyIcon();
        Timer timer = new Timer();
        HotKeyWindow hk;
        bool laptop;
        HashSet<uint> ours = new HashSet<uint>();
        Icon pcIcon, ltIcon;
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        uint myPid = (uint)Process.GetCurrentProcess().Id;
        Receiver receiver;
        string configError = "";

        public TrayContext()
        {
            pcIcon = MakeIcon(Color.FromArgb(0, 120, 215), "PC");
            ltIcon = MakeIcon(Color.FromArgb(230, 120, 0), "LT");

            LoadHotkey();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Switch audio", null, delegate { Toggle(); });
            menu.Items.Add("Set hotkey...", null, delegate { StartCapture(); });
            menu.Items.Add("Exit", null, delegate { Quit(); });
            ni.ContextMenuStrip = menu;
            ni.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(); };
            ni.Visible = true;

            hk = new HotKeyWindow(hkMod, hkVk);
            hk.Pressed += delegate { Toggle(); };

            captureTimeout.Interval = 20000;
            captureTimeout.Tick += delegate { EndCapture(); ni.ShowBalloonTip(1500, "Hotkey", "Capture timed out", ToolTipIcon.None); };

            StartReceiver();
            ShowState();
            if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--capture") StartCapture();

            timer.Interval = 1000;
            timer.Tick += delegate { Apply(); UpdateTooltip(); WriteState(); };
            timer.Start();
        }

        void StartReceiver()
        {
            string path = Path.Combine(dir, "local", "network.json");
            try
            {
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
                var lt = (Dictionary<string, object>)root["laptop"];
                string host = (string)lt["host"];
                int port = lt.ContainsKey("port") ? Convert.ToInt32(lt["port"]) : 8080;
                receiver = new Receiver(host, port);
                receiver.Start();
            }
            catch (Exception e)
            {
                configError = "config error: " + e.Message;
                ni.ShowBalloonTip(4000, "AudioSwitch", "Can't read " + path, ToolTipIcon.Warning);
            }
        }

        string ReceiverStatus { get { return receiver != null ? receiver.Status : configError; } }

        string CfgPath { get { return Path.Combine(dir, "hotkey.txt"); } }

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
            ni.ShowBalloonTip(3000, "Set hotkey", "Press the key you want now (20 s)", ToolTipIcon.None);
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
                    ni.ShowBalloonTip(2000, "Hotkey set", hkName, ToolTipIcon.None);
                }
                else
                {
                    hk.Rebind(hkMod, hkVk);
                    ni.ShowBalloonTip(2500, "Hotkey failed", hkName + " unchanged. That key can't be registered.", ToolTipIcon.None);
                }
                UpdateTooltip();
                WriteState();
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

        // Laptop mode mutes every other app on the default device; PC mode unmutes only what we muted.
        void Apply()
        {
            try
            {
                Sessions.ForEach(delegate(uint pid, SimpleAudioVolume vol)
                {
                    if (pid == myPid) return;
                    bool cur = vol.Mute;
                    if (laptop && !cur) { vol.Mute = true; ours.Add(pid); }
                    else if (!laptop && cur && ours.Contains(pid)) { vol.Mute = false; ours.Remove(pid); }
                });
            }
            catch { }
        }

        void UpdateTooltip()
        {
            string t = (laptop ? "LAPTOP" : "PC") + " | " + hkName + " | " + ReceiverStatus;
            ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void ShowState()
        {
            if (receiver != null) receiver.Feed = laptop;
            ni.Icon = laptop ? ltIcon : pcIcon;
            string body = laptop ? "LAPTOP (" + ReceiverStatus + ")" : "PC";
            ni.ShowBalloonTip(1000, "Audio source", body, ToolTipIcon.None);
            Apply();
            UpdateTooltip();
            WriteState();
        }

        void WriteState()
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "state.txt"),
                    "mode=" + (laptop ? "LAPTOP" : "PC") + " hotkey=" + hkName + (hk.Registered ? " ok" : " FAILED") +
                    " receiver=[" + ReceiverStatus + "] bytes=" + (receiver != null ? Interlocked.Read(ref receiver.Bytes) : 0) +
                    " lastcapture=[" + lastCapture + "] time=" + DateTime.Now.ToString("s"));
            }
            catch { }
        }

        void Toggle()
        {
            laptop = !laptop;
            ShowState();
        }

        void Quit()
        {
            timer.Stop();
            EndCapture();
            if (receiver != null) receiver.Stop();
            try
            {
                Sessions.ForEach(delegate(uint pid, SimpleAudioVolume vol)
                {
                    if (ours.Contains(pid)) vol.Mute = false;
                });
            }
            catch { }
            hk.Dispose();
            ni.Visible = false;
            ExitThread();
        }
    }
}
