using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioSwitch
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new System.Threading.Mutex(true, "AudioSwitchTray_Wyatt", out created))
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

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static class CoreAudio
    {
        public delegate void SessionVisitor(uint pid, ISimpleAudioVolume vol);

        public static void ForEachSession(SessionVisitor visit)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0) return;
            Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return;
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator se;
            if (mgr.GetSessionEnumerator(out se) != 0) return;
            int n;
            se.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                object so;
                if (se.GetSession(i, out so) != 0) continue;
                var c2 = so as IAudioSessionControl2;
                var vol = so as ISimpleAudioVolume;
                if (c2 == null || vol == null) continue;
                if (c2.IsSystemSoundsSession() == 0) continue;
                uint pid;
                c2.GetProcessId(out pid);
                visit(pid, vol);
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

            EnsureReader();
            ShowState();
            if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--capture") StartCapture();

            timer.Interval = 1000;
            timer.Tick += delegate { EnsureReader(); Apply(); };
            timer.Start();
        }

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
                    uint vk = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                    uint scan = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
                    bool isMod = (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
                    if (!isMod)
                    {
                        uint mod = 0;
                        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mod |= 1;
                        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mod |= 2;
                        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mod |= 4;
                        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) mod |= 8;
                        lastCapture = "vk=0x" + vk.ToString("X") + " scan=0x" + scan.ToString("X") + " mod=" + mod;
                        BeginInvoke(vk, mod);
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            };
            hookHandle = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
            captureTimeout.Start();
            ni.ShowBalloonTip(3000, "Set hotkey", "Press the key you want now (20 s)", ToolTipIcon.None);
        }

        void BeginInvoke(uint vk, uint mod)
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
                    ni.Text = "Audio: " + (laptop ? "LAPTOP" : "PC") + " (" + hkName + ")";
                    ni.ShowBalloonTip(2000, "Hotkey set", hkName, ToolTipIcon.None);
                }
                else
                {
                    hk.Rebind(hkMod, hkVk);
                    ni.ShowBalloonTip(2500, "Hotkey failed", hkName + " unchanged. That key can't be registered.", ToolTipIcon.None);
                }
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

        void EnsureReader()
        {
            if (Process.GetProcessesByName("ScreamReader").Length > 0) return;
            string p = Path.Combine(dir, "ScreamReader", "ScreamReader.exe");
            if (File.Exists(p))
                Process.Start(new ProcessStartInfo(p) { WorkingDirectory = Path.GetDirectoryName(p) });
        }

        bool IsReader(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName == "ScreamReader"; }
            catch { return false; }
        }

        void Apply()
        {
            try
            {
                CoreAudio.ForEachSession(delegate(uint pid, ISimpleAudioVolume vol)
                {
                    if (pid == myPid) return;
                    bool isReader = IsReader(pid);
                    bool shouldMute = laptop ? !isReader : isReader;
                    bool cur;
                    vol.GetMute(out cur);
                    Guid g = Guid.Empty;
                    if (shouldMute && !cur) { vol.SetMute(true, ref g); ours.Add(pid); }
                    else if (!shouldMute && cur && ours.Contains(pid)) { vol.SetMute(false, ref g); ours.Remove(pid); }
                });
            }
            catch { }
        }

        void ShowState()
        {
            string mode = laptop ? "LAPTOP" : "PC";
            ni.Icon = laptop ? ltIcon : pcIcon;
            ni.Text = "Audio: " + mode + " (" + hkName + ")";
            ni.ShowBalloonTip(1000, "Audio source", mode, ToolTipIcon.None);
            Apply();
            WriteState();
        }

        void WriteState()
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "state.txt"),
                    "mode=" + (laptop ? "LAPTOP" : "PC") + " hotkey=" + hkName + (hk.Registered ? " ok" : " FAILED") +
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
            laptop = false;
            try
            {
                CoreAudio.ForEachSession(delegate(uint pid, ISimpleAudioVolume vol)
                {
                    if (!ours.Contains(pid)) return;
                    Guid g = Guid.Empty;
                    vol.SetMute(false, ref g);
                });
            }
            catch { }
            foreach (var p in Process.GetProcessesByName("ScreamReader")) { try { p.Kill(); } catch { } }
            hk.Dispose();
            ni.Visible = false;
            ExitThread();
        }
    }
}
