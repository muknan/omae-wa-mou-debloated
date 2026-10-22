// Wallhaven Shuffle for Windows - a small tray app that changes the desktop wallpaper every few minutes, using the
// same list as the TV (a wallhaven-shuffle deployment's /api/list). It shuffles locally and shows every wallpaper
// once before repeating, pauses during games / full-screen apps, and keeps favourites.
// Built with the C# compiler that ships with Windows (.NET Framework 4.x, C# 5): see build.ps1.
// Look and feel follow Windows 11 (Fluent): light/dark theme and accent colour from Windows, 8 px window corners,
// 4 px controls, Segoe UI Variable and Segoe Fluent Icons. Wallpapers change with Windows' own cross-fade.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Wallhaven Shuffle")]
[assembly: System.Reflection.AssemblyProduct("Wallhaven Shuffle")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace WallhavenShuffle
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "WallhavenShuffle.SingleInstance", out first))
            {
                if (!first)
                {
                    // Already running: starting it again opens the window of the running copy.
                    IntPtr other = Native.FindWindow(null, TrayApp.WindowTitle);
                    if (other != IntPtr.Zero) Native.PostMessage(other, Native.ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
                catch (NotSupportedException) { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (o, e) => TrayApp.Log("error: " + e.Exception);
                Theme.Load();
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }

    // ==================================================================================================== native

    static class Native
    {
        public static readonly int ShowWindowMessage = RegisterWindowMessage("WallhavenShuffle.ShowWindow");
        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // A game, a full-screen video or a presentation is running (the same check Windows uses for "do not disturb").
        public static bool FullScreenAppRunning()
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return false;
            return state == 2 || state == 3 || state == 4 || state == 7; // busy, D3D full screen, presentation, store app
        }

        // Windows 11 window chrome: dark/light title bar in the window's own colour, rounded corners.
        public static void StyleWindow(IntPtr hwnd, bool dark, Color caption)
        {
            int on = dark ? 1 : 0, round = 2, color = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, 20, ref on, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE: round
            DwmSetWindowAttribute(hwnd, 35, ref color, 4);  // DWMWA_CAPTION_COLOR
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        }

        // ---- the desktop wallpaper

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
            uint GetMonitorDevicePathCount();
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
        }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaper { }

        // Sets the wallpaper on every monitor. Through Explorer's IDesktopWallpaper, which cross-fades from the old
        // picture like Windows' own slideshow (when "Animation effects" is on in Settings > Accessibility > Visual
        // effects); falls back to SystemParametersInfo, which switches instantly. Returns which one was used.
        public static string SetWallpaper(string file, string position)
        {
            string style = "10", tile = "0"; int pos = 4; // Fill
            switch (position)
            {
                case "Fit": style = "6"; pos = 3; break;
                case "Stretch": style = "2"; pos = 2; break;
                case "Center": style = "0"; pos = 0; break;
                case "Span": style = "22"; pos = 5; break;
                case "Tile": style = "0"; tile = "1"; pos = 1; break;
            }
            Exception error = null;
            var t = new Thread(delegate ()
            {
                object com = null;
                try
                {
                    com = new DesktopWallpaper();
                    var dw = (IDesktopWallpaper)com;
                    if (dw.GetPosition() != pos) dw.SetPosition(pos);
                    dw.SetWallpaper(null, file);
                }
                catch (Exception ex) { error = ex; }
                finally { if (com != null) Marshal.ReleaseComObject(com); }
            });
            t.SetApartmentState(ApartmentState.STA); // COM from Explorer's shell: an STA thread of its own
            t.IsBackground = true;
            t.Start();
            if (t.Join(20000) && error == null) return "fade";

            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
            {
                k.SetValue("WallpaperStyle", style);
                k.SetValue("TileWallpaper", tile);
            }
            if (!SystemParametersInfo(0x0014, 0, file, 0x01 | 0x02)) // SPI_SETDESKWALLPAPER, update ini + broadcast
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return "instant (" + (error == null ? "timeout" : error.Message) + ")";
        }
    }

    // ==================================================================================================== theme

    // Colours, fonts and drawing helpers, following Windows 11's light and dark themes and the user's accent colour.
    static class Theme
    {
        public static bool Dark;
        public static float Scale = 1f;
        public static Color Accent, OnAccent, Bg, Card, CardBorder, Field, FieldBorder, Text, TextSecondary, TextDisabled, Hover, Pressed, Divider, Menu;
        static string bodyFamily, displayFamily, iconFamily;

        public static void Load()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                Dark = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            Accent = Color.FromArgb(0, 103, 192);
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                if (k != null && k.GetValue("AccentColor") != null)
                {
                    uint abgr = unchecked((uint)Convert.ToInt32(k.GetValue("AccentColor")));
                    Accent = Color.FromArgb((int)(abgr & 0xFF), (int)((abgr >> 8) & 0xFF), (int)((abgr >> 16) & 0xFF));
                }
            // Windows uses a lighter shade of the accent in dark mode and a darker one in light mode.
            Accent = Dark ? Mix(Accent, Color.White, 0.45) : Mix(Accent, Color.Black, 0.1);
            OnAccent = Luma(Accent) > 0.6 ? Color.Black : Color.White;
            if (Dark)
            {
                Bg = Color.FromArgb(32, 32, 32); Card = Color.FromArgb(43, 43, 43); CardBorder = Color.FromArgb(29, 29, 29);
                Field = Color.FromArgb(55, 55, 55); FieldBorder = Color.FromArgb(70, 70, 70);
                Text = Color.White; TextSecondary = Color.FromArgb(200, 200, 200); TextDisabled = Color.FromArgb(120, 120, 120);
                Hover = Color.FromArgb(58, 58, 58); Pressed = Color.FromArgb(50, 50, 50); Divider = Color.FromArgb(56, 56, 56);
                Menu = Color.FromArgb(44, 44, 44);
            }
            else
            {
                Bg = Color.FromArgb(243, 243, 243); Card = Color.FromArgb(251, 251, 251); CardBorder = Color.FromArgb(229, 229, 229);
                Field = Color.White; FieldBorder = Color.FromArgb(214, 214, 214);
                Text = Color.FromArgb(26, 26, 26); TextSecondary = Color.FromArgb(96, 96, 96); TextDisabled = Color.FromArgb(160, 160, 160);
                Hover = Color.FromArgb(234, 234, 234); Pressed = Color.FromArgb(225, 225, 225); Divider = Color.FromArgb(229, 229, 229);
                Menu = Color.FromArgb(249, 249, 249);
            }
            if (bodyFamily != null) return;
            var families = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name));
            bodyFamily = families.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            displayFamily = families.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            iconFamily = families.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }

        public static Font Body(float size) { return new Font(bodyFamily, size); }
        public static Font Strong(float size) { return new Font(displayFamily, size, FontStyle.Bold); }
        public static Font Icons(float size) { return new Font(iconFamily, size); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        static double Luma(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255; }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // A Segoe Fluent Icons glyph as a small bitmap (for menu items).
        public static Bitmap Glyph(string glyph, int px, Color color)
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(iconFamily, px * 0.75f, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph, f, b, new RectangleF(0, 0, px, px), sf);
            }
            return bmp;
        }
    }

    // Segoe Fluent Icons code points used in the app.
    static class Glyphs
    {
        public const string Previous = "", Next = "", Pause = "", Play = "", Heart = "",
            HeartFill = "", Hide = "", Open = "", Settings = "", Folder = "", Power = "",
            Chevron = "", More = "", Restore = "", Edit = "";
    }

    // ==================================================================================================== data

    class Wall
    {
        public string Id, Page, Image, Resolution;
        public int Favorites;
        public string Extension { get { var e = Path.GetExtension(new Uri(Image).AbsolutePath); return string.IsNullOrEmpty(e) ? ".jpg" : e; } }
    }

    // key=value settings in settings.ini next to the exe. Everything (settings, cache, log) lives in the exe's folder:
    // install it in a normal folder you own (e.g. %USERPROFILE%\WallhavenShuffle). Not under AppData on purpose: apps
    // started from a packaged app get AppData writes redirected into that package, where Explorer can't see the
    // wallpaper file and silently shows black.
    class Settings
    {
        public int IntervalSeconds = 120;
        public string Position = "Fill";
        public int MinFavorites = 50;
        public bool PauseFullScreen = true;
        public bool PauseOnBattery = false;
        public bool Paused = false;
        public bool StartupDone = false;     // "start with Windows" is switched on once, on the first run
        public string ListUrl = "";
        public string OriginalWallpaper = "", OriginalStyle = "", OriginalTile = "";

        public static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        public DateTime Stamp;               // settings.ini's time when last read or written: edits in Notepad are picked up

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;
            s.Stamp = File.GetLastWriteTimeUtc(FilePath);
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int n;
                switch (k)
                {
                    case "IntervalSeconds": if (int.TryParse(v, out n)) s.IntervalSeconds = Math.Max(10, n); break;
                    case "Position": s.Position = v; break;
                    case "MinFavorites": if (int.TryParse(v, out n)) s.MinFavorites = Math.Max(0, n); break;
                    case "PauseFullScreen": s.PauseFullScreen = v == "1"; break;
                    case "PauseOnBattery": s.PauseOnBattery = v == "1"; break;
                    case "Paused": s.Paused = v == "1"; break;
                    case "StartupDone": s.StartupDone = v == "1"; break;
                    case "ListUrl": s.ListUrl = v; break;
                    case "OriginalWallpaper": s.OriginalWallpaper = v; break;
                    case "OriginalStyle": s.OriginalStyle = v; break;
                    case "OriginalTile": s.OriginalTile = v; break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[] {
                    "# Wallhaven Shuffle settings. Most are in the app (click the tray icon); ListUrl and MinFavorites only here.",
                    "# ListUrl: a wallhaven-shuffle /api/list address. Saved changes apply within a few seconds.",
                    "IntervalSeconds=" + IntervalSeconds, "Position=" + Position, "MinFavorites=" + MinFavorites,
                    "PauseFullScreen=" + (PauseFullScreen ? "1" : "0"), "PauseOnBattery=" + (PauseOnBattery ? "1" : "0"),
                    "Paused=" + (Paused ? "1" : "0"), "StartupDone=" + (StartupDone ? "1" : "0"), "ListUrl=" + ListUrl,
                    "OriginalWallpaper=" + OriginalWallpaper, "OriginalStyle=" + OriginalStyle, "OriginalTile=" + OriginalTile });
                Stamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (IOException) { }
        }
    }

    // ==================================================================================================== tray app

    class TrayApp : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "WallhavenShuffle";
        public const string WindowTitle = "WallhavenShuffle.Tray";
        const int KeepCached = 20;

        public readonly Settings settings = Settings.Load();
        readonly string cacheDir = Path.Combine(Settings.Dir, "cache");
        readonly string favouritesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallhaven Favourites");
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        readonly Random rng = new Random();
        readonly object gate = new object();

        List<Wall> walls = new List<Wall>();
        HashSet<string> seen, blocked;
        List<string> history;                // ids shown, newest last
        int historyPos = -1;                 // position in history while going back with "Previous"
        Wall current, prepared;
        DateTime nextChange = DateTime.Now.AddSeconds(3), nextListRefresh = DateTime.MinValue;
        bool busy, locked;
        string lastError = "";
        MainWindow window;

        public event EventHandler StateChanged;  // the window listens: new wallpaper, pause, list loaded

        public TrayApp()
        {
            Text = WindowTitle; // a second launch finds this hidden window by its title and asks it to open the window
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            CreateHandle(); // hidden forms get no handle on their own; hotkeys and BeginInvoke need one
            Directory.CreateDirectory(cacheDir);
            seen = new HashSet<string>(ReadList("seen.txt"));
            blocked = new HashSet<string>(ReadList("blocked.txt"));
            history = ReadList("history.txt").ToList();
            walls = ParseWalls(File.Exists(ListCache) ? File.ReadAllText(ListCache) : "");
            if (history.Count > 0) current = walls.FirstOrDefault(w => w.Id == history[history.Count - 1]);

            if (string.IsNullOrEmpty(settings.OriginalWallpaper))
            {
                // Remember the wallpaper from before, for "Restore my original wallpaper".
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    settings.OriginalWallpaper = Convert.ToString(k.GetValue("WallPaper", ""));
                    settings.OriginalStyle = Convert.ToString(k.GetValue("WallpaperStyle", "10"));
                    settings.OriginalTile = Convert.ToString(k.GetValue("TileWallpaper", "0"));
                }
                settings.Save();
            }
            if (!settings.StartupDone) { SetStartup(true); settings.StartupDone = true; settings.Save(); } // on by default

            tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
            tray.Visible = true;

            clock.Interval = 1000;
            clock.Tick += delegate { Tick(); };
            clock.Start();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            UpdateTooltip();
            Log("started");
            if (string.IsNullOrEmpty(settings.ListUrl)) BeginInvoke(new Action(ShowMainWindow)); // first run: needs a source
        }

        string ListCache { get { return Path.Combine(Settings.Dir, "list.json"); } }

        // ------------------------------------------------------------------------- state for the window
        public Wall Current { get { return current; } }
        public string CurrentFile { get { return current == null ? null : CachePath(current); } }
        public bool IsFavourite { get { return current != null && File.Exists(FavouritePath(current)); } }
        public bool StartsWithWindowsNow { get { return StartsWithWindows(); } }

        public string Status()
        {
            if (string.IsNullOrEmpty(settings.ListUrl)) return "Add a source: ⋯ > Advanced settings";
            if (walls.Count == 0) return string.IsNullOrEmpty(lastError) ? "Loading the wallpaper list…" : "Can't reach the source right now, retrying";
            if (settings.Paused) return "Paused";
            if (busy) return "Changing…";
            string hold = HoldReason();
            if (hold != null) return "Waiting: " + hold;
            var left = nextChange - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return "Next in " + (left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss"));
        }

        public string Stats()
        {
            if (walls.Count == 0) return "";
            var pool = Pool();
            return string.Format("{0} of {1} this round", pool.Count(w => seen.Contains(w.Id)), pool.Count);
        }

        void Changed() { UpdateTooltip(); if (StateChanged != null) StateChanged(this, EventArgs.Empty); }

        // ------------------------------------------------------------------------- window, hotkeys, menu

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); } // a tray app: never show this form

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const uint mods = 0x0001 | 0x0002 | 0x0004 | 0x4000; // Alt + Ctrl + Shift, no auto-repeat
            Native.RegisterHotKey(Handle, 1, mods, 0x27); // Right: next
            Native.RegisterHotKey(Handle, 2, mods, 0x25); // Left: previous
            Native.RegisterHotKey(Handle, 3, mods, 0x46); // F: favourite
            Native.RegisterHotKey(Handle, 4, mods, 0x50); // P: pause
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                switch (m.WParam.ToInt32()) { case 1: Next("hotkey"); break; case 2: Previous(); break; case 3: Favourite(); break; case 4: TogglePause(); break; }
                return;
            }
            if (m.Msg == Native.ShowWindowMessage && Native.ShowWindowMessage != 0) { ShowMainWindow(); return; }
            base.WndProc(ref m);
        }

        public void ShowMainWindow()
        {
            if (window == null || window.IsDisposed)
            {
                window = new MainWindow(this);
                window.Show();
                Log("window opened");
            }
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate();
            Native.SetForegroundWindow(window.Handle);
        }

        ToolStripMenuItem pauseItem, favouriteItem;

        ContextMenuStrip BuildMenu()
        {
            var menu = new FluentMenu();
            pauseItem = menu.Add("Pause", Glyphs.Pause, delegate { TogglePause(); });
            menu.Add("Next wallpaper", Glyphs.Next, delegate { Next("menu"); });
            menu.Add("Previous wallpaper", Glyphs.Previous, delegate { Previous(); });
            favouriteItem = menu.Add("Save to favourites", Glyphs.Heart, delegate { Favourite(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Add("Open Wallhaven Shuffle", Glyphs.Settings, delegate { ShowMainWindow(); });
            menu.Add("Exit", Glyphs.Power, delegate { Close(); });
            menu.Opening += delegate
            {
                pauseItem.Text = settings.Paused ? "Resume" : "Pause";
                pauseItem.Tag = settings.Paused ? Glyphs.Play : Glyphs.Pause;
                favouriteItem.Text = IsFavourite ? "Remove from favourites" : "Save to favourites";
                favouriteItem.Tag = IsFavourite ? Glyphs.HeartFill : Glyphs.Heart;
                favouriteItem.Enabled = current != null;
                menu.RefreshGlyphs();
            };
            return menu;
        }

        // ------------------------------------------------------------------------- timer

        void Tick()
        {
            UpdateTooltip();
            ReloadIfEdited();
            if (window != null && !window.IsDisposed && window.Visible) window.Tick();
            if (busy || string.IsNullOrEmpty(settings.ListUrl)) return;
            if (DateTime.Now >= nextListRefresh) { RefreshListAsync(); return; }
            if (settings.Paused || DateTime.Now < nextChange) return;
            if (HoldReason() != null) { nextChange = DateTime.Now.AddSeconds(15); return; } // try again shortly
            Next("timer");
        }

        // settings.ini was saved in an editor: take the new values.
        void ReloadIfEdited()
        {
            DateTime stamp;
            try { if (!File.Exists(Settings.FilePath) || (stamp = File.GetLastWriteTimeUtc(Settings.FilePath)) == settings.Stamp) return; }
            catch (IOException) { return; }
            var f = Settings.Load();
            bool sourceChanged = f.ListUrl != settings.ListUrl, positionChanged = f.Position != settings.Position;
            settings.IntervalSeconds = f.IntervalSeconds; settings.Position = f.Position; settings.MinFavorites = f.MinFavorites;
            settings.PauseFullScreen = f.PauseFullScreen; settings.PauseOnBattery = f.PauseOnBattery; settings.Paused = f.Paused;
            settings.ListUrl = f.ListUrl;
            Log("settings.ini edited");
            SettingsChanged(sourceChanged, positionChanged);
            if (window != null && !window.IsDisposed) window.LoadValues();
        }

        public void EditAdvanced()
        {
            settings.Save();
            try { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); } catch (Exception) { Open(Settings.FilePath); }
        }

        // Why an automatic change should wait right now, or null.
        string HoldReason()
        {
            if (locked) return "PC locked";
            if (settings.PauseFullScreen && Native.FullScreenAppRunning()) return "game or full screen";
            if (settings.PauseOnBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) return "on battery";
            return null;
        }

        void UpdateTooltip()
        {
            string text = "Wallhaven Shuffle · " + Status();
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // ------------------------------------------------------------------------- list

        void RefreshListAsync()
        {
            busy = true;
            nextListRefresh = DateTime.Now.AddHours(1);
            string url = settings.ListUrl;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = null, error = null;
                try { json = Download(url); } catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    if (json != null)
                    {
                        var list = ParseWalls(json);
                        if (list.Count > 0) { walls = list; File.WriteAllText(ListCache, json); lastError = ""; Log("list: " + list.Count + " wallpapers"); }
                    }
                    else
                    {
                        lastError = error; Log("list failed: " + error);
                        nextListRefresh = DateTime.Now.AddMinutes(2);
                    }
                    Changed();
                    bool due = current == null || !File.Exists(CachePath(current)) || DateTime.Now >= nextChange;
                    if (walls.Count > 0 && due && !settings.Paused && HoldReason() == null) Next("start");
                }));
            });
        }

        // Accepts /api/list ({ items: [...] }) and Wallhaven-style answers ({ data: [...] }).
        static List<Wall> ParseWalls(string json)
        {
            var result = new List<Wall>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return result;
                object arr;
                if (!root.TryGetValue("items", out arr)) root.TryGetValue("data", out arr);
                var items = arr as object[];
                if (items == null) return result;
                foreach (var o in items)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("id") || !d.ContainsKey("path")) continue;
                    object fav, page, res;
                    d.TryGetValue("favorites", out fav); d.TryGetValue("url", out page); d.TryGetValue("resolution", out res);
                    result.Add(new Wall
                    {
                        Id = Convert.ToString(d["id"]), Image = Convert.ToString(d["path"]),
                        Page = Convert.ToString(page), Resolution = Convert.ToString(res), Favorites = fav == null ? 0 : Convert.ToInt32(fav)
                    });
                }
            }
            catch (Exception) { result.Clear(); }
            return result;
        }

        List<Wall> Pool()
        {
            var pool = walls.Where(w => w.Favorites >= settings.MinFavorites && !blocked.Contains(w.Id)).ToList();
            return pool.Count > 0 ? pool : walls.Where(w => !blocked.Contains(w.Id)).ToList();
        }

        // A wallpaper not shown yet in this round; when all have been shown, a new round starts.
        Wall PickNext()
        {
            var pool = Pool();
            if (pool.Count == 0) return null;
            string cur = current == null ? null : current.Id;
            var fresh = pool.Where(w => !seen.Contains(w.Id) && w.Id != cur).ToList();
            if (fresh.Count == 0)
            {
                seen.Clear(); WriteList("seen.txt", seen);
                fresh = pool.Where(w => w.Id != cur).ToList();
                if (fresh.Count == 0) fresh = pool;
                Log("new round");
            }
            return fresh[rng.Next(fresh.Count)];
        }

        // ------------------------------------------------------------------------- changing the wallpaper

        public void Next(string why)
        {
            if (busy) return;
            Log("next (" + why + ")");
            if (historyPos >= 0 && historyPos < history.Count - 1)
            {
                // Came back with "Previous": go forward through history again first.
                historyPos++;
                var w = walls.FirstOrDefault(x => x.Id == history[historyPos]);
                if (w != null) { ShowWall(w, false); return; }
            }
            historyPos = -1;
            var next = prepared ?? PickNext();
            prepared = null;
            if (next == null) { if (walls.Count == 0) RefreshListAsync(); return; }
            ShowWall(next, true);
        }

        public void Previous()
        {
            if (busy || history.Count < 2) return;
            if (historyPos < 0) historyPos = history.Count - 1;
            if (historyPos == 0) return;
            historyPos--;
            var w = walls.FirstOrDefault(x => x.Id == history[historyPos]);
            if (w != null) ShowWall(w, false);
        }

        void ShowWall(Wall w, bool isNew)
        {
            busy = true;
            Changed();
            string position = settings.Position;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = null, error = null, how = null;
                try { file = Fetch(w); how = Native.SetWallpaper(file, position); }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    nextChange = DateTime.Now.AddSeconds(error == null ? settings.IntervalSeconds : 30);
                    if (error != null) { lastError = error; Log("change failed (" + w.Id + "): " + error); Changed(); return; }
                    lastError = "";
                    current = w;
                    Log("wallpaper " + w.Id + " " + how + (isNew ? "" : " (from history)"));
                    if (isNew)
                    {
                        seen.Add(w.Id); WriteList("seen.txt", seen);
                        history.Add(w.Id);
                        if (history.Count > 100) history.RemoveRange(0, history.Count - 100);
                        WriteList("history.txt", history);
                    }
                    Changed();
                    PrepareNext();
                }));
            });
        }

        // Download the next one ahead of time, so the change itself is instant.
        void PrepareNext()
        {
            var next = PickNext();
            if (next == null) return;
            prepared = next;
            var keep = new HashSet<string>(history.Skip(Math.Max(0, history.Count - KeepCached))) { next.Id };
            if (current != null) keep.Add(current.Id);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Fetch(next); } catch (Exception) { /* fetched again when it's due */ }
                try { CleanCache(keep); } catch (Exception) { /* next time */ }
            });
        }

        string CachePath(Wall w) { return Path.Combine(cacheDir, w.Id + w.Extension); }

        string Fetch(Wall w)
        {
            string path = CachePath(w);
            lock (gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
                string tmp = path + ".part";
                using (var wc = NewClient()) wc.DownloadFile(w.Image, tmp);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            return path;
        }

        // Never deletes the file Windows is showing: it reads it again later (e.g. when Explorer restarts).
        void CleanCache(HashSet<string> keep)
        {
            lock (gate)
                foreach (var f in Directory.GetFiles(cacheDir))
                    if (!keep.Contains(Path.GetFileNameWithoutExtension(f))) { try { File.Delete(f); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------------- actions

        public void TogglePause()
        {
            settings.Paused = !settings.Paused;
            settings.Save();
            if (!settings.Paused) nextChange = DateTime.Now.AddSeconds(settings.IntervalSeconds);
            Changed();
        }

        string FavouritePath(Wall w) { return Path.Combine(favouritesDir, w.Id + w.Extension); }

        public void Favourite()
        {
            if (current == null) return;
            try
            {
                Directory.CreateDirectory(favouritesDir);
                string src = CachePath(current), dst = FavouritePath(current);
                if (File.Exists(dst)) { File.Delete(dst); Log("unfavourite " + current.Id); }      // toggles
                else { File.Copy(File.Exists(src) ? src : Fetch(current), dst); Log("favourite " + current.Id); }
            }
            catch (Exception ex) { Log("favourite failed: " + ex.Message); }
            Changed();
        }

        public void Block()
        {
            if (current == null) return;
            blocked.Add(current.Id);
            WriteList("blocked.txt", blocked);
            Log("blocked " + current.Id);
            Next("hidden");
        }

        public void OpenOnWallhaven() { if (current != null) Open(current.Page); }
        public void OpenFavourites() { Directory.CreateDirectory(favouritesDir); Open(favouritesDir); }

        // Settings changed in the window: save, and apply what needs applying.
        public void SettingsChanged(bool sourceChanged, bool positionChanged)
        {
            settings.Save();
            nextChange = DateTime.Now.AddSeconds(Math.Min(settings.IntervalSeconds, Math.Max(0, (nextChange - DateTime.Now).TotalSeconds)));
            prepared = null;
            if (walls.Count == 0 || sourceChanged) nextListRefresh = DateTime.MinValue;
            if (positionChanged && current != null && !busy && File.Exists(CachePath(current)))
            {
                string file = CachePath(current), position = settings.Position;
                ThreadPool.QueueUserWorkItem(delegate { try { Native.SetWallpaper(file, position); } catch (Exception ex) { Log("fit failed: " + ex.Message); } });
            }
            Changed();
        }

        public void RestoreOriginal()
        {
            settings.Paused = true;
            settings.Save();
            string file = settings.OriginalWallpaper;
            string position = settings.OriginalTile == "1" ? "Tile" : settings.OriginalStyle == "6" ? "Fit" : settings.OriginalStyle == "2" ? "Stretch" :
                settings.OriginalStyle == "0" ? "Center" : settings.OriginalStyle == "22" ? "Span" : "Fill";
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                ThreadPool.QueueUserWorkItem(delegate { try { Native.SetWallpaper(file, position); } catch (Exception ex) { Log("restore failed: " + ex.Message); } });
            Log("restored the original wallpaper (paused)");
            Changed();
        }

        static bool StartsWithWindows()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null;
        }

        public static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue(AppName) != null) k.DeleteValue(AppName);
            }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { OnUserPreferenceChanged(sender, e); })); return; }
            Theme.Load();                     // light/dark or accent colour changed
            if (window != null && !window.IsDisposed) window.ApplyTheme();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i);
            tray.Visible = false;
            tray.Dispose();
            Log("exit");
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------------- helpers

        static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "WallhavenShuffle-Windows/2.0";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        static string Download(string url) { using (var wc = NewClient()) return wc.DownloadString(url); }

        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { /* nothing to open it with */ }
        }

        static IEnumerable<string> ReadList(string name)
        {
            string p = Path.Combine(Settings.Dir, name);
            return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Trim().Length > 0) : Enumerable.Empty<string>();
        }

        static void WriteList(string name, IEnumerable<string> items)
        {
            try { File.WriteAllLines(Path.Combine(Settings.Dir, name), items.ToArray()); } catch (IOException) { }
        }

        static readonly object logGate = new object();
        public static void Log(string msg)
        {
            lock (logGate)
                try
                {
                    string p = Path.Combine(Settings.Dir, "log.txt");
                    var lines = File.Exists(p) ? File.ReadAllLines(p).ToList() : new List<string>();
                    lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg);
                    if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
                    File.WriteAllLines(p, lines.ToArray());
                }
                catch (IOException) { }
        }
    }

    // ==================================================================================================== main window

    // The one window, kept calm: the wallpaper, one row of controls, and five settings that apply instantly.
    // Less common things live in the "⋯" menu. Laid out in 96-DPI pixels and scaled once (fonts are in points).
    class MainWindow : Form
    {
        static readonly int[] Intervals = { 30, 60, 120, 180, 300, 600, 900, 1800, 3600, 7200, 21600, 43200, 86400 };
        static readonly string[] Positions = { "Fill", "Fit", "Stretch", "Center", "Span" };
        const int W = 480, Gutter = 24;

        readonly TrayApp app;
        readonly Settings s;
        readonly Preview preview = new Preview();
        readonly Label status = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        readonly RoundButton favBtn = new RoundButton(Glyphs.Heart, 40, "Save to favourites  (Ctrl+Alt+Shift+F)");
        readonly RoundButton prevBtn = new RoundButton(Glyphs.Previous, 44, "Previous  (Ctrl+Alt+Shift+←)");
        readonly RoundButton playBtn = new RoundButton(Glyphs.Pause, 56, "Pause  (Ctrl+Alt+Shift+P)") { Primary = true };
        readonly RoundButton nextBtn = new RoundButton(Glyphs.Next, 44, "Next wallpaper  (Ctrl+Alt+Shift+→)");
        readonly RoundButton moreBtn = new RoundButton(Glyphs.More, 40, "More");
        readonly DropDown every = new DropDown(), fit = new DropDown();
        readonly Toggle pauseGames = new Toggle(), pauseBattery = new Toggle(), startup = new Toggle();
        readonly ToolTip tips = new ToolTip();
        readonly FluentMenu moreMenu = new FluentMenu();
        ToolStripMenuItem hideItem, openItem;
        string shownFile;
        bool loading;

        public MainWindow(TrayApp owner)
        {
            app = owner; s = owner.settings;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Wallhaven Shuffle";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body(10f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;

            int y = Gutter, inner = W - 2 * Gutter;
            preview.Bounds = new Rectangle(Gutter, y, inner, inner * 9 / 16); y = preview.Bottom + 14;

            var transport = new RoundButton[] { favBtn, prevBtn, playBtn, nextBtn, moreBtn };
            int gap = 14, total = transport.Sum(b => b.Width) + gap * (transport.Length - 1), x = (W - total) / 2;
            foreach (var b in transport) { b.Location = new Point(x, y + (56 - b.Height) / 2); x += b.Width + gap; }
            y += 56 + 6;
            status.Bounds = new Rectangle(Gutter, y, inner, 22); y = status.Bottom + 18;

            var card = new Card { Bounds = new Rectangle(Gutter, y, inner, 0) };
            card.AddRow("Change wallpaper every", every);
            card.AddRow("Picture fit", fit);
            card.AddRow("Pause during games and full screen", pauseGames);
            card.AddRow("Pause on battery", pauseBattery);
            card.AddRow("Start with Windows", startup);
            y = card.Bottom + Gutter;

            Controls.AddRange(new Control[] { preview, status, card });
            Controls.AddRange(transport);
            ClientSize = new Size(W, y);
            Scale(new SizeF(Theme.Scale, Theme.Scale));
            ClientSize = new Size((int)(W * Theme.Scale), (int)(y * Theme.Scale));

            // The "⋯" menu: the less common actions.
            hideItem = moreMenu.Add("Never show this one", Glyphs.Hide, delegate { app.Block(); });
            openItem = moreMenu.Add("Open on Wallhaven", Glyphs.Open, delegate { app.OpenOnWallhaven(); });
            moreMenu.Add("Open favourites folder", Glyphs.Folder, delegate { app.OpenFavourites(); });
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Add("Restore my original wallpaper", Glyphs.Restore, delegate { app.RestoreOriginal(); });
            moreMenu.Add("Advanced settings", Glyphs.Edit, delegate { app.EditAdvanced(); });
            moreMenu.Opening += delegate { hideItem.Enabled = openItem.Enabled = app.Current != null; moreMenu.RefreshGlyphs(); };

            foreach (var i in Intervals) every.Items.Add(new KeyValuePair<string, object>(IntervalText(i), i));
            foreach (var p in Positions) fit.Items.Add(new KeyValuePair<string, object>(p, p));
            LoadValues();

            every.Changed += delegate { if (loading) return; s.IntervalSeconds = (int)every.Value; app.SettingsChanged(false, false); };
            fit.Changed += delegate { if (loading) return; s.Position = (string)fit.Value; app.SettingsChanged(false, true); };
            pauseGames.CheckedChanged += delegate { if (loading) return; s.PauseFullScreen = pauseGames.Checked; app.SettingsChanged(false, false); };
            pauseBattery.CheckedChanged += delegate { if (loading) return; s.PauseOnBattery = pauseBattery.Checked; app.SettingsChanged(false, false); };
            startup.CheckedChanged += delegate { if (loading) return; TrayApp.SetStartup(startup.Checked); };

            favBtn.Click += delegate { app.Favourite(); };
            prevBtn.Click += delegate { app.Previous(); };
            playBtn.Click += delegate { app.TogglePause(); };
            nextBtn.Click += delegate { app.Next("window"); };
            moreBtn.Click += delegate { moreMenu.Show(moreBtn, new Point(0, moreBtn.Height + (int)(4 * Theme.Scale))); };
            preview.Click += delegate { app.OpenOnWallhaven(); };
            foreach (var b in transport) tips.SetToolTip(b, b.Tip);
            tips.SetToolTip(preview, "Open on Wallhaven");

            app.StateChanged += OnStateChanged;
            ApplyTheme();
            UpdateView(true);
        }

        static string IntervalText(int sec)
        {
            if (sec < 60) return sec + " seconds";
            if (sec < 3600) return (sec / 60) + (sec == 60 ? " minute" : " minutes");
            return (sec / 3600) + (sec == 3600 ? " hour" : " hours");
        }

        // Shows the current settings (also after settings.ini was edited).
        public void LoadValues()
        {
            loading = true;
            if (!every.Items.Any(i => (int)i.Value == s.IntervalSeconds))
                every.Items.Add(new KeyValuePair<string, object>(IntervalText(s.IntervalSeconds), s.IntervalSeconds));
            every.SelectValue(s.IntervalSeconds);
            fit.SelectValue(s.Position);
            pauseGames.Checked = s.PauseFullScreen;
            pauseBattery.Checked = s.PauseOnBattery;
            startup.Checked = app.StartsWithWindowsNow;
            loading = false;
        }

        // Opens just above the notification area, like a flyout.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var area = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(12 * Theme.Scale);
            Location = new Point(area.Right - Width - m, Math.Max(area.Top, area.Bottom - Height - m));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleWindow(Handle, Theme.Dark, Theme.Bg);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            status.BackColor = Theme.Bg; status.ForeColor = Theme.TextSecondary;
            if (IsHandleCreated) Native.StyleWindow(Handle, Theme.Dark, Theme.Bg);
            foreach (Control c in AllControls(this)) c.Invalidate();
            Invalidate();
        }

        static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls) { yield return c; foreach (var d in AllControls(c)) yield return d; }
        }

        void OnStateChanged(object sender, EventArgs e) { if (!IsDisposed) UpdateView(false); }

        public void Tick()
        {
            string stats = app.Stats();
            status.Text = app.Status() + (stats.Length > 0 ? "  ·  " + stats : "");
        }

        void UpdateView(bool force)
        {
            var w = app.Current;
            preview.Overlay = w == null ? null : w.Resolution.Replace("x", " × ") + "     ♥ " + w.Favorites.ToString("N0");
            playBtn.Glyph = s.Paused ? Glyphs.Play : Glyphs.Pause;
            tips.SetToolTip(playBtn, s.Paused ? "Resume  (Ctrl+Alt+Shift+P)" : "Pause  (Ctrl+Alt+Shift+P)");
            favBtn.Glyph = app.IsFavourite ? Glyphs.HeartFill : Glyphs.Heart;
            favBtn.Active = app.IsFavourite;
            tips.SetToolTip(favBtn, app.IsFavourite ? "Remove from favourites  (Ctrl+Alt+Shift+F)" : "Save to favourites  (Ctrl+Alt+Shift+F)");
            favBtn.Enabled = w != null;
            Tick();
            string file = app.CurrentFile;
            if (force || file != shownFile)
            {
                shownFile = file;
                preview.SetImage(file != null && File.Exists(file) ? file : null);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            app.StateChanged -= OnStateChanged;
            preview.SetImage(null);
            moreMenu.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ==================================================================================================== controls

    abstract class FluentControl : Control
    {
        protected bool hover, down;
        protected FluentControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
        protected static float K { get { return Theme.Scale; } }
        protected Color Surface { get { return Parent != null && (Parent is Row || Parent.Parent is Row) ? Theme.Card : Theme.Bg; } }

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (!Focused || !ShowFocusCues) return;
            using (var p = new Pen(Theme.Text, 1.5f * K)) using (var path = Theme.Rounded(RectangleF.Inflate(r, -1, -1), radius)) g.DrawPath(p, path);
        }
    }

    // The current wallpaper, rounded like a Windows 11 card, with resolution and favourites in a small pill.
    // Keeps a downscaled copy only (a 4K bitmap is 33 MB).
    class Preview : Control
    {
        Bitmap thumb;
        string overlay;
        public string Overlay { get { return overlay; } set { if (overlay == value) return; overlay = value; Invalidate(); } }

        public Preview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public void SetImage(string file)
        {
            var old = thumb; thumb = null;
            if (old != null) old.Dispose();
            if (file != null)
            {
                try
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var src = Image.FromStream(fs, false, false))
                    {
                        int w = (int)(432 * 1.5 * Theme.Scale), h = Math.Max(1, (int)(w * src.Height / (double)src.Width));
                        thumb = new Bitmap(w, h);
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                    }
                }
                catch (Exception) { thumb = null; }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 8 * k))
            {
                g.SetClip(path);
                if (thumb != null)
                {
                    double scale = Math.Max(Width / (double)thumb.Width, Height / (double)thumb.Height); // "Fill", centred
                    float w = (float)(thumb.Width * scale), h = (float)(thumb.Height * scale);
                    g.DrawImage(thumb, (Width - w) / 2, (Height - h) / 2, w, h);
                }
                else
                {
                    using (var b = new LinearGradientBrush(ClientRectangle, Theme.Mix(Theme.Accent, Theme.Bg, 0.55), Theme.Mix(Theme.Accent, Theme.Bg, 0.85), 120f))
                        g.FillRectangle(b, ClientRectangle);
                    TextRenderer.DrawText(g, "Your wallpaper will show up here", Font, ClientRectangle, Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (overlay != null)
                    using (var f = Theme.Strong(9f))
                    {
                        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                        Size ts = TextRenderer.MeasureText(g, overlay, f, Size.Empty, flags);
                        var pill = new RectangleF(12 * k, Height - 12 * k - ts.Height - 10 * k, ts.Width + 22 * k, ts.Height + 10 * k);
                        using (var pp = Theme.Rounded(pill, pill.Height / 2))
                        using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                            g.FillPath(b, pp);
                        TextRenderer.DrawText(g, overlay, f, new Point((int)(pill.X + 11 * k), (int)(pill.Y + 5 * k)), Color.White, flags);
                    }
                g.ResetClip();
                using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 30 : 24, Theme.Dark ? Color.White : Color.Black))) g.DrawPath(pen, path);
            }
        }
    }

    // A rounded card of setting rows with thin dividers (Windows 11 Settings style).
    class Card : Panel
    {
        const int RowHeight = 56, Pad = 4;
        readonly List<Row> rows = new List<Row>();

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void AddRow(string title, Control control)
        {
            var row = new Row(title, control, rows.Count > 0) { Bounds = new Rectangle(1, Pad + rows.Count * RowHeight, Width - 2, RowHeight) };
            rows.Add(row);
            Controls.Add(row);
            Height = Pad * 2 + rows.Count * RowHeight;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * Theme.Scale))
            using (var fill = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // One setting: the label on the left, its control on the right.
    class Row : Control
    {
        readonly bool divider;
        readonly Control control;

        public Row(string title, Control control, bool divider)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = title; this.divider = divider; this.control = control;
            Controls.Add(control);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float k = Theme.Scale;
            bool toggle = control is Toggle;
            int w = (int)((toggle ? 92 : 172) * k), h = (int)((toggle ? 32 : 34) * k);
            control.Bounds = new Rectangle(Width - w - (int)(16 * k), (Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale;
            g.Clear(Theme.Card);
            var text = new Rectangle((int)(20 * k), 0, control.Left - (int)(32 * k), Height);
            TextRenderer.DrawText(g, Text, Font, text, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (divider) using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    // Windows 11 toggle switch, with its "On" / "Off" label on the left.
    class Toggle : FluentControl
    {
        bool on;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = 40 * K, h = 20 * K;
            var r = new RectangleF(Width - w - 4 * K, (Height - h) / 2, w, h);
            TextRenderer.DrawText(g, on ? "On" : "Off", Font, new Rectangle(0, 0, (int)(r.X - 10 * K), Height), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var path = Theme.Rounded(r, h / 2))
            {
                if (on) using (var b = new SolidBrush(hover ? Theme.Mix(Theme.Accent, Theme.Text, 0.1) : Theme.Accent)) g.FillPath(b, path);
                else
                {
                    using (var b = new SolidBrush(hover ? Theme.Hover : Theme.Field)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.TextSecondary, 1f * K)) g.DrawPath(p, path);
                }
            }
            float d = (down ? 14 : hover ? 13 : 12) * K;
            float x = on ? r.Right - d - 4 * K : r.X + 4 * K;
            using (var b = new SolidBrush(on ? Theme.OnAccent : Theme.TextSecondary)) g.FillEllipse(b, x, r.Y + (r.Height - d) / 2, d, d);
            FocusRing(g, RectangleF.Inflate(r, 3 * K, 3 * K), h / 2 + 3 * K);
        }
    }

    // A compact dropdown: the choice and a chevron; opens a menu of the options.
    class DropDown : FluentControl
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public event EventHandler Changed;
        int index = -1;
        public object Value { get { return index >= 0 ? Items[index].Value : null; } }

        public void SelectValue(object value)
        {
            index = Items.FindIndex(i => Equals(i.Value, value));
            if (index < 0 && Items.Count > 0) index = 0;
            Invalidate();
        }

        protected override void OnClick(EventArgs e) { Focus(); OpenMenu(); base.OnClick(e); }

        void OpenMenu()
        {
            var menu = new FluentMenu { ShowImageMargin = false, ShowCheckMargin = true };
            for (int i = 0; i < Items.Count; i++)
            {
                int n = i;
                var item = new ToolStripMenuItem(Items[i].Key) { Checked = i == index, Padding = new Padding(4, 6, 4, 6) };
                item.Click += delegate { if (index == n) return; index = n; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); };
                menu.Items.Add(item);
            }
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            menu.MinimumSize = new Size(Width, 0);
            menu.Show(this, new Point(0, Height + (int)(4 * K)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(r, 4 * K))
            using (var fill = new SolidBrush(down ? Theme.Pressed : hover ? Theme.Hover : Theme.Field))
            using (var pen = new Pen(Theme.FieldBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            string text = index >= 0 ? Items[index].Key : "";
            TextRenderer.DrawText(g, text, Font, new Rectangle((int)(12 * K), 0, Width - (int)(40 * K), Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (var f = Theme.Icons(8f))
                TextRenderer.DrawText(g, Glyphs.Chevron, f, new Rectangle(Width - (int)(32 * K), 0, (int)(20 * K), Height), Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            FocusRing(g, r, 4 * K);
        }
    }

    // A round icon button (a Segoe Fluent Icons glyph). Primary = filled with the accent colour.
    class RoundButton : FluentControl
    {
        string glyph;
        public bool Primary, Active;
        public readonly string Tip;
        public string Glyph { get { return glyph; } set { if (glyph == value) return; glyph = value; Invalidate(); } }

        public RoundButton(string glyph, int size, string tip) { this.glyph = glyph; Tip = tip; Size = new Size(size, size); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, fg;
            if (Primary) { fill = down ? Theme.Mix(Theme.Accent, Theme.Bg, 0.2) : hover ? Theme.Mix(Theme.Accent, Theme.Text, 0.12) : Theme.Accent; fg = Theme.OnAccent; }
            else { fill = down ? Theme.Pressed : hover ? Theme.Hover : Theme.Bg; fg = Active ? Theme.Accent : Theme.Text; }
            if (!Enabled) { fill = Theme.Bg; fg = Theme.TextDisabled; }
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
            using (var f = Theme.Icons(Primary ? 14f : 12f))
                TextRenderer.DrawText(g, glyph, f, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Text, 1.5f * K)) g.DrawEllipse(p, RectangleF.Inflate(r, -1, -1));
        }
    }

    // A menu in the Windows 11 style, with Segoe Fluent Icons glyphs (kept in each item's Tag).
    class FluentMenu : ContextMenuStrip
    {
        public FluentMenu()
        {
            Renderer = new FluentMenuRenderer();
            Padding = new Padding(4, 6, 4, 6);
            Font = Theme.Body(10f);
            ShowImageMargin = true;
            ShowCheckMargin = false;
        }

        public ToolStripMenuItem Add(string text, string glyph, EventHandler click)
        {
            var i = new ToolStripMenuItem(text, null, click) { Padding = new Padding(6, 7, 16, 7), Tag = glyph };
            Items.Add(i);
            return i;
        }

        public void RefreshGlyphs()
        {
            int px = (int)Math.Round(16 * Theme.Scale);
            ImageScalingSize = new Size(px, px);
            foreach (ToolStripItem i in Items)
                if (i.Tag is string)
                {
                    if (i.Image != null) i.Image.Dispose();
                    i.Image = Theme.Glyph((string)i.Tag, px, i.Enabled ? Theme.Text : Theme.TextDisabled);
                }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle); }
        protected override void OnOpening(System.ComponentModel.CancelEventArgs e) { base.OnOpening(e); if (Items.Count > 0 && Items[0].Image == null && ShowImageMargin) RefreshGlyphs(); }
    }

    // ==================================================================================================== menus

    // Windows 11 style menus: theme colours, rounded highlight, roomy rows. The rounded window corners and the
    // border come from Windows itself (DWMWA_WINDOW_CORNER_PREFERENCE).
    class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        public FluentMenuRenderer() : base(new FluentColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Menu); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 4 * Theme.Scale))
            using (var b = new SolidBrush(Theme.Hover))
                g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool shortcut = e.TextRectangle.X > e.Item.Width / 2; // the shortcut column, drawn dimmer
            e.TextColor = !e.Item.Enabled ? Theme.TextDisabled : shortcut ? Theme.TextSecondary : Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Divider)) e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale;
            var r = e.ImageRectangle;
            if (e.Item.Image != null) { g.DrawImage(e.Item.Image, r); return; } // the glyph shows the state (accent when on)
            // The selected choice: a small accent pill, as in Windows 11 lists.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Accent))
            using (var path = Theme.Rounded(new RectangleF(r.X - 2 * k, r.Y + r.Height / 2f - 8 * k, 3 * k, 16 * k), 1.5f * k))
                g.FillPath(b, path);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }

        class FluentColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return Theme.Menu; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Menu; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Menu; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Menu; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Menu; } }
            public override Color SeparatorDark { get { return Theme.Divider; } }
            public override Color SeparatorLight { get { return Theme.Divider; } }
            public override Color CheckBackground { get { return Theme.Menu; } }
            public override Color CheckSelectedBackground { get { return Theme.Hover; } }
            public override Color CheckPressedBackground { get { return Theme.Hover; } }
        }
    }
}
