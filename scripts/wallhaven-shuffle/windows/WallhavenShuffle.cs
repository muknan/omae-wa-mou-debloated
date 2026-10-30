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
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Wallhaven Shuffle")]
[assembly: System.Reflection.AssemblyProduct("Wallhaven Shuffle")]
[assembly: System.Reflection.AssemblyVersion("2.1.0.0")]

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
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (o, e) => TrayApp.Log("error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (o, e) => TrayApp.Log("crash: " + e.ExceptionObject);
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
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        // The app's icon at the tray's size (24 px at 150 %), from the exe's own icon, which has that size: sharper
        // than shrinking the 32 px one Icon.ExtractAssociatedIcon gives.
        public static Icon SmallAppIcon()
        {
            var small = new IntPtr[1];
            try { if (ExtractIconEx(Application.ExecutablePath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) return Icon.FromHandle(small[0]); }
            catch (Exception) { }
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        // A game, a full-screen video or a presentation is running (the same check Windows uses for "do not disturb").
        public static bool FullScreenAppRunning()
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return false;
            return state == 2 || state == 3 || state == 4 || state == 7; // busy, D3D full screen, presentation, store app
        }

        // "Animation effects" in Settings > Accessibility > Visual effects.
        public static bool AnimationsOn()
        {
            bool on = true;
            SystemParametersInfo(0x1042, 0, ref on, 0); // SPI_GETCLIENTAREAANIMATION
            return on;
        }

        // Windows 11 flyout chrome: rounded corners and a thin border drawn by Windows.
        public static void StyleFlyout(IntPtr hwnd, bool dark, Color border)
        {
            int on = dark ? 1 : 0, round = 2, color = border.R | (border.G << 8) | (border.B << 16);
            DwmSetWindowAttribute(hwnd, 20, ref on, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE: round
            DwmSetWindowAttribute(hwnd, 34, ref color, 4);  // DWMWA_BORDER_COLOR
        }

        // The acrylic (blurred, tinted) backdrop flyouts use, behind the whole window. False where it isn't available.
        public static bool Acrylic(IntPtr hwnd)
        {
            int type = 3; // DWMSBT_TRANSIENTWINDOW
            if (DwmSetWindowAttribute(hwnd, 38, ref type, 4) != 0) return false;
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            return DwmExtendFrameIntoClientArea(hwnd, ref m) == 0;
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        }

        // ---- files: a second name for the same file (no copy), and the Recycle Bin

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newName, string existing, IntPtr security);

        // dst becomes another name for src's data (an NTFS hard link): it shows up in that folder like any picture but
        // takes no extra space, and stays valid when src is deleted. Only within one drive; elsewhere it's copied.
        // True if linked, false if copied.
        public static bool LinkOrCopy(string src, string dst)
        {
            if (CreateHardLink(dst, src, IntPtr.Zero)) return true;
            File.Copy(src, dst);
            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom, pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        // Moves a file or folder to the Recycle Bin, without asking or showing progress. False if it couldn't.
        public static bool Recycle(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3,                                   // FO_DELETE
                pFrom = Path.GetFullPath(path) + "\0\0",
                fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400   // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
        }

        // ---- copying a bitmap to a window with its alpha channel (GDI drawing would wipe it)

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, IntPtr count);

        // bmp must be 32 bpp premultiplied ARGB, the size of the area to paint.
        public static void BlitWithAlpha(Graphics target, Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            IntPtr hdc = target.GetHdc();
            try
            {
                IntPtr bits, mem = CreateCompatibleDC(hdc), dib = CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { CopyMemory(bits, data.Scan0, new IntPtr(w * h * 4)); } finally { bmp.UnlockBits(data); }
                IntPtr old = SelectObject(mem, dib);
                BitBlt(hdc, 0, 0, w, h, mem, 0, 0, 0x00CC0020); // SRCCOPY
                SelectObject(mem, old);
                DeleteObject(dib);
                DeleteDC(mem);
            }
            finally { target.ReleaseHdc(hdc); }
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

        // Sets the wallpaper on every monitor, through Explorer's IDesktopWallpaper (SystemParametersInfo if that
        // fails). Explorer cross-fades to the new picture, but only once the desktop has its separate wallpaper layer
        // (a WorkerW window). Explorer creates that layer when Progman gets message 0x052C, the same one
        // live-wallpaper apps use; without it the change is instant.
        public static void SetWallpaper(string file, string position)
        {
            IntPtr progman = FindWindow("Progman", null), r;
            if (progman != IntPtr.Zero) SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out r);
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
            if (t.Join(20000) && error == null) return;

            using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                k.SetValue("WallpaperStyle", style);
                k.SetValue("TileWallpaper", tile);
            }
            if (!SystemParametersInfo(0x0014, 0, file, 0x01 | 0x02)) // SPI_SETDESKWALLPAPER, update ini + broadcast
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // ==================================================================================================== lock screen

    // The Windows lock screen picture, through Windows' own API (Windows.System.UserProfile.LockScreen). Called through
    // reflection so the app still builds with just the .NET Framework compiler. Blocks; call from a worker thread.
    static class LockScreenPicture
    {
        public static void Set(string file)
        {
            var storageFile = Type.GetType("Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime", true);
            var lockScreen = Type.GetType("Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType=WindowsRuntime", true);
            var ext = Type.GetType("System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", true);
            var asTask = ext.GetMethods().First(m => m.Name == "AsTask" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");
            var asTaskAction = ext.GetMethods().First(m => m.Name == "AsTask" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "IAsyncAction");
            var open = (Task)asTask.MakeGenericMethod(storageFile).Invoke(null, new[] { storageFile.GetMethod("GetFileFromPathAsync").Invoke(null, new object[] { file }) });
            if (!open.Wait(15000)) throw new TimeoutException("opening the picture took too long");
            object picture = open.GetType().GetProperty("Result").GetValue(open, null);
            var apply = (Task)asTaskAction.Invoke(null, new[] { lockScreen.GetMethod("SetImageFileAsync").Invoke(null, new[] { picture }) });
            if (!apply.Wait(30000)) throw new TimeoutException("Windows took too long to take the picture");
        }
    }

    // ==================================================================================================== theme

    // Colours, fonts and drawing helpers, following Windows 11's light and dark themes and the user's accent colour.
    // The flyout is acrylic on Windows 11: its surfaces are the translucent layers WinUI uses (white or black at a few
    // percent) over the blur. Without acrylic the same layers sit on a solid background.
    static class Theme
    {
        public static bool Dark, Glass;
        public static bool NoAcrylic;        // Windows refused the acrylic backdrop once: don't count on it again
        public static float Scale = 1f;
        public static Color Accent, OnAccent, Bg, Menu, FlyoutBorder, Card, CardBorder, Field, FieldHover, FieldPressed, FieldBorder,
            Text, TextSecondary, TextDisabled, Hover, Pressed, Divider, ToggleOff;
        static string bodyFamily, displayFamily, iconFamily;

        public static void Load()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            bool transparency = true;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                Dark = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
                transparency = k == null || Convert.ToInt32(k.GetValue("EnableTransparency", 1)) != 0;
            }
            int build = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                if (k != null) int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber", "0")), out build);
            Glass = transparency && build >= 22621 && !NoAcrylic;
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
            Color w = Color.White, b = Color.Black;
            if (Dark)
            {
                Bg = Color.FromArgb(32, 32, 32); Menu = Color.FromArgb(44, 44, 44); FlyoutBorder = Color.FromArgb(70, 70, 70);
                Card = A(13, w); CardBorder = A(26, b); Divider = A(21, w);
                Field = A(15, w); FieldHover = A(21, w); FieldPressed = A(8, w); FieldBorder = A(20, w);
                Hover = A(15, w); Pressed = A(10, w); ToggleOff = A(25, b);
                Text = w; TextSecondary = A(197, w); TextDisabled = A(93, w);
            }
            else
            {
                Bg = Color.FromArgb(243, 243, 243); Menu = Color.FromArgb(249, 249, 249); FlyoutBorder = Color.FromArgb(214, 214, 214);
                Card = A(179, w); CardBorder = A(15, b); Divider = A(15, b);
                Field = A(179, w); FieldHover = A(128, Color.FromArgb(249, 249, 249)); FieldPressed = A(77, Color.FromArgb(249, 249, 249)); FieldBorder = A(15, b);
                Hover = A(10, b); Pressed = A(6, b); ToggleOff = A(6, b);
                Text = A(228, b); TextSecondary = A(158, b); TextDisabled = A(92, b);
            }
            if (bodyFamily != null) return;
            var families = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name));
            bodyFamily = families.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            displayFamily = families.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            iconFamily = families.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }

        static Color A(int alpha, Color c) { return Color.FromArgb(alpha, c); }

        public static Font Body(float size) { return new Font(bodyFamily, size); }
        public static Font Strong(float size) { return new Font(displayFamily, size, FontStyle.Bold); }

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

        // ---- text: GDI+ (keeps the alpha channel), one line, centred on the capitals

        static StringFormat Format(StringAlignment h)
        {
            var f = new StringFormat(StringFormat.GenericTypographic) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            f.FormatFlags |= StringFormatFlags.NoWrap;
            return f;
        }

        public static void DrawText(Graphics g, string text, Font f, RectangleF r, Color color, StringAlignment h)
        {
            r.Offset(0, Nudge(f, g.DpiY));
            using (var b = new SolidBrush(color)) using (var sf = Format(h)) g.DrawString(text, f, b, r, sf);
        }

        public static SizeF Measure(Graphics g, string text, Font f)
        {
            using (var sf = Format(StringAlignment.Near)) return g.MeasureString(text, f, PointF.Empty, sf);
        }

        // A line of text is centred on its line box, which has room for accents above and descenders below, so the
        // capitals sit a little high. This measures, once per font, how far down to move it so they sit in the middle.
        static readonly Dictionary<string, float> nudges = new Dictionary<string, float>();
        static float Nudge(Font f, float dpi)
        {
            string key = f.Name + "|" + f.SizeInPoints + "|" + f.Style + "|" + dpi;
            float n;
            if (nudges.TryGetValue(key, out n)) return n;
            int h = (int)Math.Ceiling(f.SizeInPoints * dpi / 72 * 3);
            using (var bmp = new Bitmap(h, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                bmp.SetResolution(dpi, dpi);
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(Color.Black))
                using (var sf = Format(StringAlignment.Center))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString("H", f, b, new RectangleF(0, 0, h, h), sf);
                }
                int top = -1, bottom = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < h; x++)
                        if (bmp.GetPixel(x, y).A > 100) { if (top < 0) top = y; bottom = y; break; }
                n = top < 0 ? 0 : (float)Math.Round(h / 2.0 - (top + bottom + 1) / 2.0);
            }
            nudges[key] = n;
            return n;
        }

        // ---- icons: Segoe Fluent Icons glyphs, centred on their actual outline (not every glyph is centred in its box)

        public static float Px(Graphics g, float points) { return points * g.DpiY / 72f; }

        public static void DrawGlyph(Graphics g, string glyph, float emPx, RectangleF r, Color color)
        {
            using (var path = new GraphicsPath())
            using (var family = new FontFamily(iconFamily))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            using (var b = new SolidBrush(color))
            {
                path.AddString(glyph, family, 0, emPx, PointF.Empty, sf);
                var bb = path.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate((float)Math.Round(r.X + (r.Width - bb.Width) / 2 - bb.X), (float)Math.Round(r.Y + (r.Height - bb.Height) / 2 - bb.Y));
                    path.Transform(m);
                }
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
                g.SmoothingMode = smoothing;
            }
        }

        // A glyph as a small bitmap (for menu items).
        public static Bitmap Glyph(string glyph, int px, Color color)
        {
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) DrawGlyph(g, glyph, px * 0.8f, new RectangleF(0, 0, px, px), color);
            return bmp;
        }
    }

    // Segoe Fluent Icons code points used in the app.
    static class Glyphs
    {
        public const string Previous = "", Next = "", Pause = "", Play = "", Heart = "",
            HeartFill = "", Hide = "", Open = "", Settings = "", Folder = "", Power = "",
            Chevron = "", More = "", Restore = "", Edit = "", View = "",
            Pin = "", PinFill = "", Add = "", Library = "", FolderOpen = "",
            NewFolder = "", Rename = "", Delete = "", Shuffle = "", Globe = "", Pictures = "",
            Check = "";
    }

    // ==================================================================================================== data

    class Wall
    {
        public string Id, Page, Image, Resolution, Extension;
        public int Favorites;
        public string LocalPath;             // a picture in a folder (favourites, a playlist, your own): shown from there, never downloaded
        public bool IsLocal { get { return LocalPath != null; } }

        // Ids and extensions name the files in the cache, so only plain ones are taken (letters, digits, - and _).
        static readonly System.Text.RegularExpressions.Regex SafeId = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_-]{1,40}$");
        public static readonly HashSet<string> ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

        // A picture in a folder. Its id is its full path (so rounds and history work as for the list). Pictures this
        // app saved are named after their Wallhaven id: those keep their Wallhaven page, and the list's details if the
        // list has them.
        public static Wall Local(string path, Wall fromList)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            bool wallhaven = SafeId.IsMatch(name) && name.Length <= 12;
            return new Wall
            {
                Id = path, LocalPath = path, Extension = Path.GetExtension(path).ToLowerInvariant(),
                Page = fromList != null ? fromList.Page : wallhaven ? "https://wallhaven.cc/w/" + name : null,
                Resolution = fromList != null ? fromList.Resolution : "", Favorites = fromList != null ? fromList.Favorites : -1
            };
        }

        // The name a picture gets when it's saved to favourites or a playlist.
        public string FileName { get { return IsLocal ? Path.GetFileName(LocalPath) : Id + Extension; } }
        // The Wallhaven id, also for a saved picture named after it; null for other pictures.
        public string WallhavenId
        {
            get
            {
                if (!IsLocal) return Id;
                string name = Path.GetFileNameWithoutExtension(LocalPath);
                return Page != null && Page.EndsWith("/" + name) ? name : null;
            }
        }
        public static bool IsImageFile(string path) { return ImageTypes.Contains(Path.GetExtension(path)); }

        // A usable entry of the list, or null.
        public static Wall Make(string id, string image, string page, string resolution, int favorites)
        {
            Uri uri;
            if (id == null || !SafeId.IsMatch(id) || !Uri.TryCreate(image, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return null;
            string ext = Path.GetExtension(uri.AbsolutePath);
            return new Wall { Id = id, Image = image, Page = page, Resolution = resolution ?? "", Favorites = favorites, Extension = ImageTypes.Contains(ext) ? ext.ToLowerInvariant() : ".jpg" };
        }

        // A file in the cache folder that this app put there (anything else in that folder is left alone).
        public static bool IsCacheFile(string name)
        {
            string ext = Path.GetExtension(name);
            return ImageTypes.Contains(ext) && SafeId.IsMatch(Path.GetFileNameWithoutExtension(name));
        }
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
        public string CacheDir = "";         // where the originals are kept; empty = "cache" next to the exe
        public int CacheLimitGB = 10;        // the whole 1-year list is about 7 GB
        public bool LockScreen = false;      // also put each wallpaper on the lock screen
        public string Source = "";           // what it shuffles: "" = the list, "favourites", "playlist:<name>", "folder:<path>"
        public string KeepId = "";           // "Keep this wallpaper": the one kept on screen (no automatic changes); "" = none
        public string PlaylistsDir = "";     // where playlists are made; empty = "Playlists" next to the cache folder (same drive: linked, not copied)

        public static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        public string CacheFolder { get { return string.IsNullOrEmpty(CacheDir) ? Path.Combine(Dir, "cache") : Environment.ExpandEnvironmentVariables(CacheDir); } }
        public string PlaylistsFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(PlaylistsDir)) return Environment.ExpandEnvironmentVariables(PlaylistsDir);
                string parent = Path.GetDirectoryName(Path.GetFullPath(CacheFolder).TrimEnd('\\'));
                return Path.Combine(string.IsNullOrEmpty(parent) ? Dir : parent, "Playlists");
            }
        }
        public DateTime Stamp;               // settings.ini's time when last read or written: edits in Notepad are picked up

        public static readonly string[] Positions = { "Fill", "Fit", "Stretch", "Tile", "Center", "Span" };

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;
            s.Stamp = File.GetLastWriteTimeUtc(FilePath);
            string[] lines = null;
            for (int attempt = 0; lines == null; attempt++)   // an editor may be saving it right now
                try { lines = File.ReadAllLines(FilePath); }
                catch (IOException) { if (attempt >= 4) throw; Thread.Sleep(150); }
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int n;
                switch (k)
                {
                    case "IntervalSeconds": if (int.TryParse(v, out n)) s.IntervalSeconds = Math.Max(10, n); break;
                    case "Position": s.Position = Positions.FirstOrDefault(p => p.Equals(v, StringComparison.OrdinalIgnoreCase)) ?? "Fill"; break;
                    case "MinFavorites": if (int.TryParse(v, out n)) s.MinFavorites = Math.Max(0, n); break;
                    case "PauseFullScreen": s.PauseFullScreen = v == "1"; break;
                    case "PauseOnBattery": s.PauseOnBattery = v == "1"; break;
                    case "Paused": s.Paused = v == "1"; break;
                    case "StartupDone": s.StartupDone = v == "1"; break;
                    case "ListUrl": s.ListUrl = v; break;
                    case "OriginalWallpaper": s.OriginalWallpaper = v; break;
                    case "OriginalStyle": s.OriginalStyle = v; break;
                    case "OriginalTile": s.OriginalTile = v; break;
                    case "CacheDir": s.CacheDir = v; break;
                    case "CacheLimitGB": if (int.TryParse(v, out n)) s.CacheLimitGB = Math.Max(1, n); break;
                    case "LockScreen": s.LockScreen = v == "1"; break;
                    case "Source": s.Source = v; break;
                    case "KeepId": s.KeepId = v; break;
                    case "PlaylistsDir": s.PlaylistsDir = v; break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                TrayApp.WriteFileSafely(FilePath, string.Join(Environment.NewLine, new[] {
                    "# Wallhaven Shuffle settings. Most are in the app (right-click the tray icon); ListUrl and MinFavorites only here.",
                    "# ListUrl: a wallhaven-shuffle /api/list address. Saved changes apply within a few seconds.",
                    "IntervalSeconds=" + IntervalSeconds, "Position=" + Position, "MinFavorites=" + MinFavorites,
                    "PauseFullScreen=" + (PauseFullScreen ? "1" : "0"), "PauseOnBattery=" + (PauseOnBattery ? "1" : "0"),
                    "Paused=" + (Paused ? "1" : "0"), "StartupDone=" + (StartupDone ? "1" : "0"), "ListUrl=" + ListUrl,
                    "OriginalWallpaper=" + OriginalWallpaper, "OriginalStyle=" + OriginalStyle, "OriginalTile=" + OriginalTile,
                    "# Downloaded originals are kept here (empty = a cache folder next to the app), up to CacheLimitGB.",
                    "CacheDir=" + CacheDir, "CacheLimitGB=" + CacheLimitGB, "LockScreen=" + (LockScreen ? "1" : "0"),
                    "# Source: what it shuffles (empty = the list; favourites; playlist:<name>; folder:<path>). KeepId: the wallpaper kept on screen.",
                    "Source=" + Source, "KeepId=" + KeepId,
                    "# Playlists are folders in PlaylistsDir (empty = Playlists next to the cache folder: on the same drive, pictures are linked, not copied).",
                    "PlaylistsDir=" + PlaylistsDir }) + Environment.NewLine);
                Stamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (IOException) { }
        }
    }

    // ==================================================================================================== rotation

    // What comes next, and the way back. Each wallpaper in the pool is shown once per round, in random order. The
    // round's order is kept (seen.txt), so the counter is a position: Previous counts down, Next counts up, and a new
    // round starts (at 1) only when its first wallpaper is on screen. History (history.txt, the last 100, across
    // rounds) is what Previous walks back through; Next walks forward through it again before new ones come.
    // Plain logic, no files or UI, so it can be tested on its own.
    class Rotation
    {
        public readonly List<string> Round = new List<string>();     // shown this round, in order
        readonly HashSet<string> inRound = new HashSet<string>();
        public readonly List<string> History = new List<string>();   // shown, newest last
        public int Back = -1;                                         // going back: the History index on screen; -1 = at the newest
        readonly Random rng;

        public Rotation(IEnumerable<string> round, IEnumerable<string> history, int seed)
        {
            foreach (var id in round) if (inRound.Add(id)) Round.Add(id);
            History.AddRange(history);
            rng = seed == 0 ? new Random() : new Random(seed);
        }

        public bool InRound(string id) { return id != null && inRound.Contains(id); }

        // Each source (the list, favourites, a playlist) has its own round: switching swaps it in. History is shared,
        // so Previous goes back through what was really on screen.
        public void LoadRound(IEnumerable<string> round)
        {
            Round.Clear(); inRound.Clear();
            foreach (var id in round) if (inRound.Add(id)) Round.Add(id);
            Back = -1;
        }

        // The next new one: one not shown yet this round (when all were, the next round begins), and where possible
        // not one of the last few shown, so the end of one round and the start of the next don't repeat. Changes
        // nothing (it's also used to get the next one ready ahead of time).
        public string PickNew(IList<string> pool, string current)
        {
            if (pool.Count == 0) return null;
            var fresh = pool.Where(id => !inRound.Contains(id) && id != current).ToList();
            if (fresh.Count == 0) fresh = pool.Where(id => id != current).ToList();   // a new round
            if (fresh.Count == 0) fresh = pool.ToList();                              // a pool of one
            int avoid = Math.Min(20, pool.Count / 2);
            var recent = new HashSet<string>(History.Skip(Math.Max(0, History.Count - avoid)));
            var notRecent = fresh.Where(id => !recent.Contains(id)).ToList();
            if (notRecent.Count > 0) fresh = notRecent;
            return fresh[rng.Next(fresh.Count)];
        }

        // A new one is on screen. True if it started a new round (every one in the pool had been shown).
        public bool ShownNew(string id, ICollection<string> pool)
        {
            bool newRound = false;
            if (inRound.Contains(id) && pool.All(inRound.Contains)) { Round.Clear(); inRound.Clear(); newRound = true; }
            if (inRound.Add(id)) Round.Add(id);
            History.Add(id);
            if (History.Count > 100) History.RemoveRange(0, History.Count - 100);
            Back = -1;
            return newRound;
        }

        // Where Previous goes (a History index), or -1 at the oldest.
        public int BackTarget() { int at = Back < 0 ? History.Count - 1 : Back; return at > 0 ? at - 1 : -1; }
        // Where Next goes while going back (a History index), or -1 when not going back.
        public int ForwardTarget() { return Back < 0 || Back + 1 >= History.Count ? -1 : Back + 1; }
        // That History entry is on screen now (reaching the newest ends going back).
        public void ShownFromHistory(int index) { Back = index >= History.Count - 1 ? -1 : index; }

        // Position of id in this round (1 = first), counting only ones still in the pool; 0 if it isn't in this round.
        public int Position(string id, ICollection<string> pool)
        {
            int i = id == null ? -1 : Round.IndexOf(id);
            if (i < 0) return 0;
            int n = 0;
            for (int k = 0; k <= i; k++) if (pool.Contains(Round[k])) n++;
            return n;
        }

        public int ShownThisRound(ICollection<string> pool) { return Round.Count(pool.Contains); }

        // The counter for the one on screen: at the newest, how many were shown this round (also right when a spare
        // from the cache repeated an earlier one); going back, that one's place in the round; 0 = from the last round.
        public int Counter(string id, ICollection<string> pool)
        {
            if (!InRound(id)) return 0;
            return Back < 0 ? ShownThisRound(pool) : Position(id, pool);
        }

        public bool CanGoBack { get { return BackTarget() >= 0; } }
    }

    // ==================================================================================================== tray app

    class TrayApp : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "WallhavenShuffle";
        public const string WindowTitle = "WallhavenShuffle.Tray";
        const int KeepCached = 20;

        public readonly Settings settings = Settings.Load();
        string cacheDir;
        bool listComplete = true;            // the list in use was read in full (only then are files of dropped wallpapers deleted)
        readonly string favouritesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallhaven Favourites");
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        readonly Random rng = new Random();
        readonly object gate = new object();

        List<Wall> walls = new List<Wall>();
        HashSet<string> blocked;
        Rotation rot;                        // this round's order, history, going back and forth
        Wall current, prepared;
        DateTime nextChange = DateTime.Now.AddSeconds(3), nextListRefresh = DateTime.MinValue;
        bool busy, locked;                   // busy = a wallpaper change is under way
        int change;                          // numbers each change; a late answer from an abandoned one is ignored
        int queued;                          // Next (+1) / Previous (-1) pressed during a change: done right after it
        bool listBusy;                       // the list is being downloaded (doesn't hold up changes)
        DateTime busySince, listBusySince;
        int lockScreenBusy;                  // a lock screen update is under way (they run on the side)
        string lastError = "";               // the last thing that went wrong, for the status line
        bool changeFailed;                   // the last change didn't work (it's retried shortly)
        readonly Dictionary<string, Task<string>> downloads = new Dictionary<string, Task<string>>();   // under way, by id
        MainWindow window;
        readonly List<string> folders = new List<string>();   // your own folders, used like playlists (folders.txt)
        List<Wall> folderPool;               // the pictures of the folder being shuffled, re-read now and then
        string folderPoolKey;
        DateTime folderPoolTime;
        bool keepAfterChange;                // "Keep this wallpaper" pressed during a change: keep the one coming

        public event EventHandler StateChanged;  // the window listens: new wallpaper, pause, list loaded

        public TrayApp()
        {
            Text = WindowTitle; // a second launch finds this hidden window by its title and asks it to open the window
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            CreateHandle(); // hidden forms get no handle on their own; hotkeys and BeginInvoke need one
            cacheDir = settings.CacheFolder;
            string defaultCache = Path.Combine(Settings.Dir, "cache");
            try { MoveCache(defaultCache, cacheDir); }
            catch (Exception ex)
            {
                // The cache folder's drive isn't there (an external drive, a renamed folder): use the one next to the
                // app for now, and leave everything as it is.
                Log("cache folder " + cacheDir + " can't be used (" + ex.Message + "); using " + defaultCache + " this time");
                cacheDir = defaultCache;
                Directory.CreateDirectory(cacheDir);
            }
            folders.AddRange(ReadList("folders.txt"));
            if (settings.Source.StartsWith("folder:") && !folders.Contains(settings.Source.Substring(7))) settings.Source = "";
            rot = new Rotation(ReadList(RoundFile), ReadList("history.txt"), 0);
            blocked = new HashSet<string>(ReadList("blocked.txt"));
            string cachedList = "";
            try { if (File.Exists(ListCache)) cachedList = File.ReadAllText(ListCache); } catch (IOException) { }
            walls = ParseWalls(cachedList, out listComplete);
            if (rot.History.Count > 0) current = FindWall(rot.History[rot.History.Count - 1]);
            if (settings.KeepId.Length > 0)
            {
                // Kept on screen when the app last ran: carry on keeping it, if it's still there.
                var kept = FindWall(settings.KeepId);
                if (kept != null && File.Exists(CachePath(kept))) { current = kept; Log("still keeping " + Describe(kept)); }
                else { Log("the kept wallpaper (" + settings.KeepId + ") isn't there any more; shuffling again"); settings.KeepId = ""; settings.Save(); }
            }

            if (string.IsNullOrEmpty(settings.OriginalWallpaper)) RememberOriginal();
            if (!settings.StartupDone)
            {
                // On by default, but never taking over another copy's entry (a second copy elsewhere, e.g. a fresh
                // build, would otherwise redirect "Start with Windows" to itself).
                if (StartupEntry() == null || !File.Exists(StartupEntry())) SetStartup(true);
                settings.StartupDone = true; settings.Save();
            }
            // Windows re-encodes every wallpaper as a JPEG at quality 85 (a 5 MB original ends up around 1 MB, with
            // banding in dark gradients) unless JPEGImportQuality says otherwise. 100 keeps the original's quality.
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                if (k != null && !(k.GetValue("JPEGImportQuality") is int && (int)k.GetValue("JPEGImportQuality") == 100))
                {
                    k.SetValue("JPEGImportQuality", 100, RegistryValueKind.DWord);
                    Log("asked Windows to keep wallpapers at full quality (JPEGImportQuality=100)");
                }

            tray.Icon = Native.SmallAppIcon();
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) Next("double-click"); }; // the window: right-click menu
            tray.Visible = true;

            clock.Interval = 1000;
            clock.Tick += delegate { Tick(); };
            clock.Start();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            UpdateTooltip();
            Log("started");
            if (!HasSource) BeginInvoke(new Action(ShowMainWindow)); // first run: needs a source
            else BeginInvoke(new Action(delegate { window = new MainWindow(this); window.CreateControl(); })); // ready ahead of time
        }

        string ListCache { get { return Path.Combine(Settings.Dir, "list.json"); } }

        // ------------------------------------------------------------------------- state for the window
        public Wall Current { get { return current; } }
        public string CurrentFile { get { return current == null ? null : CachePath(current); } }
        public bool IsFavourite { get { return current != null && File.Exists(FavouritePath(current)); } }
        public bool StartsWithWindowsNow { get { return StartsWithWindows(); } }
        public bool CanGoBack { get { return rot.CanGoBack; } }
        public int HiddenCount { get { return blocked.Count; } }
        // "Keep this wallpaper" is on for the one on screen.
        public bool IsKept { get { return current != null && settings.KeepId.Length > 0 && settings.KeepId == current.Id; } }

        public string Status()
        {
            if (!HasSource)
                return IsListSource ? "Add a source: ⋯ > Advanced settings" : "Can't find the folder of \"" + SourceName(settings.Source) + "\"";
            if (IsListSource && walls.Count == 0) return listBusy || string.IsNullOrEmpty(lastError) ? "Loading the wallpaper list…" : "Can't reach the source right now, retrying";
            if (IsKept) return busy ? "Changing…" : "Keeping this wallpaper";
            if (!IsListSource && Pool().Count == 0)
                return settings.Source == "favourites" ? "No favourites yet: ♡ saves the one on screen" : "\"" + SourceName(settings.Source) + "\" is empty: add wallpapers with Add to playlist";
            if (settings.Paused) return "Paused";
            if (busy) return "Changing…";
            string hold = HoldReason();
            if (hold != null) return "Waiting: " + hold;
            var left = nextChange - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string when = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
            return (changeFailed ? "Couldn't change, trying again in " : "Next in ") + when;
        }

        // Where the one on screen is in this round: "12 of 1182".
        public string Stats()
        {
            if (IsListSource && walls.Count == 0) return "";
            var pool = Pool();
            if (pool.Count == 0) return "";
            if (current == null) return pool.Count.ToString("N0") + " wallpapers";
            int n = rot.Counter(current.Id, new HashSet<string>(pool.Select(w => w.Id)));
            return n > 0 ? string.Format("{0:N0} of {1:N0}", n, pool.Count) : "from the last round";
        }

        void Changed() { UpdateTooltip(); if (StateChanged != null) StateChanged(this, EventArgs.Empty); }

        // ------------------------------------------------------------------------- window, hotkeys, menu

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); } // a tray app: never show this form

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const uint mods = 0x0001 | 0x0002 | 0x0004 | 0x4000; // Alt + Ctrl + Shift, no auto-repeat
            var keys = new[] { 0x27u, 0x25u, 0x46u, 0x50u, 0x4Bu };   // Right: next, Left: previous, F: favourite, P: pause, K: keep
            var names = new[] { "→", "←", "F", "P", "K" };
            for (int i = 0; i < keys.Length; i++)
                if (!Native.RegisterHotKey(Handle, i + 1, mods, keys[i])) Log("Ctrl+Alt+Shift+" + names[i] + " is taken by another app");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                switch (m.WParam.ToInt32()) { case 1: Next("hotkey"); break; case 2: Previous(); break; case 3: Favourite(); break; case 4: TogglePause(); break; case 5: ToggleKeep(); break; }
                return;
            }
            if (m.Msg == Native.ShowWindowMessage && Native.ShowWindowMessage != 0) { ShowMainWindow(); return; }
            base.WndProc(ref m);
        }

        public void ShowMainWindow()
        {
            if (window == null || window.IsDisposed) window = new MainWindow(this);
            window.Open();
            Log("window opened");
        }

        ToolStripMenuItem pauseItem, keepItem, favouriteItem, previousItem, nextItem, addToItem;

        ContextMenuStrip BuildMenu()
        {
            var menu = new FluentMenu();
            keepItem = menu.Add("Keep this wallpaper", Glyphs.Pin, delegate { ToggleKeep(); });
            pauseItem = menu.Add("Pause", Glyphs.Pause, delegate { TogglePause(); });
            nextItem = menu.Add("Next wallpaper", Glyphs.Next, delegate { Next("menu"); });
            previousItem = menu.Add("Previous wallpaper", Glyphs.Previous, delegate { Previous(); });
            menu.AddSeparator();
            favouriteItem = menu.Add("Save to favourites", Glyphs.Heart, delegate { Favourite(); });
            addToItem = menu.AddSubmenu("Add to playlist", Glyphs.Add, sub => FillAddToMenu(sub, null));
            menu.AddSubmenu("Shuffle from", Glyphs.Shuffle, sub => FillSourceMenu(sub, null));
            menu.AddSeparator();
            menu.Add("Open Wallhaven Shuffle", Glyphs.Settings, delegate { ShowMainWindow(); });
            menu.Add("Exit", Glyphs.Power, delegate { Close(); });
            menu.Opening += delegate
            {
                keepItem.Text = IsKept ? "Stop keeping this wallpaper" : "Keep this wallpaper";
                keepItem.Tag = IsKept ? Glyphs.PinFill : Glyphs.Pin;
                keepItem.Enabled = current != null;
                pauseItem.Text = settings.Paused ? "Resume" : "Pause";
                pauseItem.Tag = settings.Paused ? Glyphs.Play : Glyphs.Pause;
                favouriteItem.Text = IsFavourite ? "Remove from favourites" : "Save to favourites";
                favouriteItem.Tag = IsFavourite ? Glyphs.HeartFill : Glyphs.Heart;
                favouriteItem.Enabled = addToItem.Enabled = current != null;
                previousItem.Enabled = CanGoBack;
                nextItem.Enabled = HasSource;
                menu.RefreshGlyphs();
            };
            return menu;
        }

        // "Add to playlist ▸": every playlist, ticked if the one on screen is in it (clicking a ticked one takes it out),
        // then "New playlist…" (made and the one on screen added).
        public void FillAddToMenu(FluentMenu sub, Form owner)
        {
            var playlists = Sources().Where(x => x.Own).ToList();
            foreach (var p in playlists)
            {
                var pl = p;
                bool inIt = current != null && InPlaylist(pl, current);
                sub.Add(pl.Name, inIt ? Glyphs.Check : Glyphs.Library, delegate { TogglePlaylist(pl); });
            }
            if (playlists.Count > 0) sub.AddSeparator();
            sub.Add("New playlist…", Glyphs.Add, delegate { NewPlaylist(true, owner); });
        }

        // "Shuffle from ▸": the list, favourites, playlists and your folders; the one in use is marked.
        public void FillSourceMenu(FluentMenu sub, Form owner)
        {
            var all = Sources();
            for (int i = 0; i < all.Count; i++)
            {
                var src = all[i];
                if (i == 2) sub.AddSeparator();   // after Wallhaven and Favourites
                var item = sub.Add(src.Name, src.Glyph, delegate { SetSource(src.Key); });
                item.Checked = src.Key == settings.Source;
            }
            sub.AddSeparator();
            sub.Add("New playlist…", Glyphs.Add, delegate { NewPlaylist(false, owner); });
            sub.Add("Add a folder of your pictures…", Glyphs.NewFolder, delegate { AddFolderDialog(owner); });
        }

        // ------------------------------------------------------------------------- timer

        void Tick()
        {
            UpdateTooltip();
            ReloadIfEdited();
            if (window != null && !window.IsDisposed && window.Visible) window.Tick();
            // Watchdog: every step has its own time limit, but nothing may keep the app stuck.
            if (busy && DateTime.Now - busySince > TimeSpan.FromMinutes(2))
            {
                busy = false; change++; queued = 0;   // its answer, if it ever comes, is ignored
                Log("a change took over 2 minutes; carrying on");
                nextChange = DateTime.Now;
            }
            if (listBusy && DateTime.Now - listBusySince > TimeSpan.FromMinutes(6)) { listBusy = false; Log("the list download took over 6 minutes; carrying on"); }
            // The list is kept fresh whatever is being shuffled (playlists use its details, and it's ready to go back to).
            if (!string.IsNullOrEmpty(settings.ListUrl) && !listBusy && DateTime.Now >= nextListRefresh) RefreshListAsync();
            if (busy || !HasSource || IsKept) return;
            if (IsListSource ? walls.Count == 0 : Pool().Count == 0) return;
            if (settings.Paused || DateTime.Now < nextChange) return;
            if (HoldReason() != null) { nextChange = DateTime.Now.AddSeconds(15); return; } // try again shortly
            if (!IsListSource && current != null && Pool().Count == 1 && Pool()[0].Id == current.Id)
            {
                nextChange = DateTime.Now.AddSeconds(settings.IntervalSeconds);   // a playlist of one: it's on screen already
                return;
            }
            Next("timer");
        }

        // settings.ini was saved in an editor: take the new values.
        void ReloadIfEdited()
        {
            Settings f;
            try
            {
                if (!File.Exists(Settings.FilePath) || File.GetLastWriteTimeUtc(Settings.FilePath) == settings.Stamp) return;
                f = Settings.Load();
            }
            catch (Exception) { return; }   // still being saved: next second
            bool sourceChanged = f.ListUrl != settings.ListUrl, positionChanged = f.Position != settings.Position;
            settings.IntervalSeconds = f.IntervalSeconds; settings.Position = f.Position; settings.MinFavorites = f.MinFavorites;
            settings.PauseFullScreen = f.PauseFullScreen; settings.PauseOnBattery = f.PauseOnBattery; settings.Paused = f.Paused;
            settings.ListUrl = f.ListUrl;
            settings.CacheLimitGB = f.CacheLimitGB; settings.LockScreen = f.LockScreen; // CacheDir: takes effect on restart
            settings.PlaylistsDir = f.PlaylistsDir;
            settings.KeepId = f.KeepId == "" || (current != null && f.KeepId == current.Id) ? f.KeepId : settings.KeepId;   // only the one on screen can be kept
            Log("settings.ini edited");
            if (f.Source != settings.Source && SourceKnown(f.Source)) SetSource(f.Source);
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

        // "Wallhaven Shuffle · Next in 1:23", with the playlist's name when one is being shuffled (63 characters at most).
        void UpdateTooltip()
        {
            string text = "Wallhaven Shuffle · " + Status();
            if (!IsListSource && HasSource) text += " · " + SourceName(settings.Source);
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;
        }

        // ------------------------------------------------------------------------- list

        void RefreshListAsync()
        {
            if (listBusy || string.IsNullOrEmpty(settings.ListUrl)) return;
            listBusy = true; listBusySince = DateTime.Now;
            nextListRefresh = DateTime.Now.AddHours(1);
            string url = settings.ListUrl;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = null, error = null;
                try { json = DownloadList(url); } catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    listBusy = false;
                    if (url != settings.ListUrl) { nextListRefresh = DateTime.MinValue; return; }   // the source changed meanwhile
                    bool complete = true;
                    var list = json == null ? new List<Wall>() : ParseWalls(json, out complete);
                    if (list.Count > 0 && (complete || list.Count >= walls.Count))
                    {
                        int before = walls.Count;
                        walls = list; listComplete = complete; lastError = "";
                        WriteFileSafely(ListCache, json);
                        Log("list: " + list.Count + " wallpapers" + (complete ? "" : " (partial)") + (before > 0 && before != list.Count ? " (was " + before + ")" : ""));
                    }
                    else if (list.Count > 0)
                    {
                        // The service is still filling in pages Wallhaven refused for a moment: keep the fuller list.
                        Log("list partial (" + list.Count + "), keeping the last full one (" + walls.Count + ")");
                        nextListRefresh = DateTime.Now.AddMinutes(5);
                    }
                    else
                    {
                        lastError = error ?? "the source sent no wallpapers";
                        Log("list failed: " + lastError);
                        nextListRefresh = DateTime.Now.AddMinutes(walls.Count == 0 ? 2 : 10);
                    }
                    folderPool = null;   // pictures in playlists pick up the list's details
                    Changed();
                    if (!IsListSource || IsKept) return;   // playlists change on the timer; a kept one stays
                    bool due = current == null || !File.Exists(CachePath(current)) || DateTime.Now >= nextChange;
                    if (walls.Count > 0 && due && !busy && !settings.Paused && HoldReason() == null) Next("start");
                    else if (!busy && prepared == null) PrepareNext();
                });
            });
        }

        // Accepts /api/list ({ items: [...], complete }) and Wallhaven-style answers ({ data: [...] }).
        static List<Wall> ParseWalls(string json, out bool complete)
        {
            var result = new List<Wall>();
            complete = true;
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return result;
                object arr, done;
                if (root.TryGetValue("complete", out done) && done is bool) complete = (bool)done;
                if (!root.TryGetValue("items", out arr)) root.TryGetValue("data", out arr);
                var items = arr as object[];
                if (items == null) return result;
                var ids = new HashSet<string>();
                foreach (var o in items)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("id") || !d.ContainsKey("path")) continue;
                    object fav, page, res;
                    d.TryGetValue("favorites", out fav); d.TryGetValue("url", out page); d.TryGetValue("resolution", out res);
                    int favorites = 0;
                    try { if (fav != null) favorites = Convert.ToInt32(fav); } catch (Exception) { }   // one odd entry doesn't spoil the list
                    var w = Wall.Make(Convert.ToString(d["id"]), Convert.ToString(d["path"]), Convert.ToString(page), Convert.ToString(res), favorites);
                    if (w != null && ids.Add(w.Id)) result.Add(w);
                }
            }
            catch (Exception) { result.Clear(); }
            return result;
        }

        // What's being shuffled: the list (above the favourites minimum, if any are), or the pictures in the chosen
        // folder. "Never show this one" applies to both.
        List<Wall> Pool()
        {
            if (!IsListSource) return FolderPool().Where(w => !blocked.Contains(w.Id)).ToList();
            var pool = walls.Where(w => w.Favorites >= settings.MinFavorites && !blocked.Contains(w.Id)).ToList();
            return pool.Count > 0 ? pool : walls.Where(w => !blocked.Contains(w.Id)).ToList();
        }

        // The pictures in the folder being shuffled (top level; hidden files and unfinished downloads left out). Read
        // again every 20 seconds, so pictures copied in by hand show up, and straight after the app changes it.
        List<Wall> FolderPool()
        {
            if (folderPool != null && folderPoolKey == settings.Source && DateTime.Now - folderPoolTime < TimeSpan.FromSeconds(20)) return folderPool;
            var list = new List<Wall>();
            string folder = SourceFolder(settings.Source);
            try
            {
                if (folder != null && Directory.Exists(folder))
                {
                    var byId = new Dictionary<string, Wall>();
                    foreach (var w in walls) byId[w.Id] = w;
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
                    {
                        if (!Wall.IsImageFile(f.Name) || (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || f.Length == 0) continue;
                        Wall m;
                        byId.TryGetValue(Path.GetFileNameWithoutExtension(f.Name), out m);
                        list.Add(Wall.Local(f.FullName, m));
                    }
                }
            }
            catch (Exception ex) { Log("couldn't read " + folder + ": " + ex.Message); }
            list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            folderPool = list; folderPoolKey = settings.Source; folderPoolTime = DateTime.Now;
            return list;
        }

        // A wallpaper by its id: one from the list, a picture in a folder (its id is its path), or one that left the
        // list but is still downloaded (a kept one, or one in history). Null if it isn't anywhere any more.
        Wall FindWall(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.IndexOf('\\') >= 0)
            {
                if (!File.Exists(id)) return null;
                string name = Path.GetFileNameWithoutExtension(id);
                return Wall.Local(id, walls.FirstOrDefault(x => x.Id == name));
            }
            var w = walls.FirstOrDefault(x => x.Id == id);
            if (w != null) return w;
            try
            {
                string file = Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, id + ".*").FirstOrDefault(f => Wall.IsCacheFile(Path.GetFileName(f))) : null;
                if (file != null) return new Wall { Id = id, Extension = Path.GetExtension(file).ToLowerInvariant(), Page = "https://wallhaven.cc/w/" + id, Resolution = "", Favorites = -1 };
            }
            catch (Exception) { }
            return null;
        }

        static string Describe(Wall w) { return w.IsLocal ? Path.GetFileName(w.LocalPath) : w.Id; }

        // The next new wallpaper (see Rotation.PickNew). Changes nothing.
        Wall PickNext()
        {
            var pool = Pool();
            string id = rot.PickNew(pool.Select(w => w.Id).ToList(), current == null ? null : current.Id);
            return id == null ? null : pool.First(w => w.Id == id);
        }

        // ------------------------------------------------------------------------- changing the wallpaper

        // Presses during a change aren't lost: they're counted and done one after another once it's on screen
        // (a few at most, so holding the hotkey down doesn't run away). The timer never queues.
        bool Queue(int step)
        {
            if (!busy) return false;
            queued = Math.Max(-5, Math.Min(5, queued + step));
            return true;
        }

        public void Next(string why)
        {
            if (busy) { if (why != "timer") Queue(+1); return; }
            if (!HasSource) { if (IsListSource) ShowMainWindow(); else Changed(); return; }   // nothing to show yet: say where to add a source
            ReleaseKeep(why);   // moving on takes "Keep this wallpaper" off
            Log("next (" + why + ")");
            // Went back with Previous: walk forward again first (skipping any that aren't there any more).
            for (int i = rot.ForwardTarget(); i >= 0 && i < rot.History.Count; i++)
            {
                var w = FindWall(rot.History[i]);
                if (w != null) { ShowWall(w, i); return; }
            }
            rot.Back = -1;
            if (prepared != null && (blocked.Contains(prepared.Id) || (current != null && prepared.Id == current.Id) || !InPool(prepared))) prepared = null;
            var next = prepared ?? PickNext();
            prepared = null;
            if (next == null) { if (IsListSource) RefreshListAsync(); else Changed(); return; }
            ShowWall(next, -1);
        }

        public void Previous()
        {
            if (Queue(-1)) return;
            for (int i = rot.BackTarget(); i >= 0; i--)   // skipping any that aren't there any more
            {
                var w = FindWall(rot.History[i]);
                if (w != null) { ReleaseKeep("previous"); ShowWall(w, i); return; }
            }
        }

        bool InPool(Wall w) { return Pool().Any(x => x.Id == w.Id); }

        // Shows a new wallpaper (fromHistory -1) or the History entry at fromHistory. Where we are (round, history)
        // only moves once it's on screen, so a failed change leaves the counter and the picture in step.
        void ShowWall(Wall w, int fromHistory)
        {
            bool isNew = fromHistory < 0;
            busy = true; busySince = DateTime.Now;
            int id = ++change;
            Changed();
            bool fade = Native.AnimationsOn(); // Windows cross-fades when "Animation effects" is on
            bool lockScreen = settings.LockScreen;
            // If this one can't be downloaded (offline, e.g. just after waking up), one already on disk is used instead.
            // Unseen ones first; if every cached one was shown this round already, any of them (but not the current one).
            string cur = current == null ? null : current.Id;
            var spare = isNew && !w.IsLocal ? Pool().Where(x => x.Id != w.Id && x.Id != cur && File.Exists(CachePath(x)))
                .OrderBy(x => rot.InRound(x.Id) ? 1 : 0).ThenBy(x => rng.Next()).Take(5).ToList() : new List<Wall>();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null, how = "", file = null;
                Wall shown = w;
                try
                {
                    try { file = Fetch(w); }
                    catch (Exception ex)
                    {
                        shown = spare.FirstOrDefault(x => File.Exists(CachePath(x)));
                        if (shown == null) throw;
                        file = CachePath(shown);
                        how = "offline (" + w.Id + ": " + ex.Message + "), from the cache: ";
                    }
                    if (id != change) return;   // abandoned meanwhile (watchdog, "Restore my original wallpaper")
                    Native.SetWallpaper(file, settings.Position);   // read now: the fit may have been changed meanwhile
                    how += fade ? "fade" : "instant";
                }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    if (id != change) return;
                    busy = false;
                    // Counted from when this change started, not when it finished, so the time a change takes (the
                    // fade, a download) doesn't push every later one back a little.
                    nextChange = error != null ? DateTime.Now.AddSeconds(30)
                        : new DateTime(Math.Max(busySince.AddSeconds(settings.IntervalSeconds).Ticks, DateTime.Now.AddSeconds(5).Ticks));
                    changeFailed = error != null;
                    if (error != null)
                    {
                        lastError = error; queued = 0; keepAfterChange = false;
                        if (w.IsLocal) folderPool = null;   // e.g. the picture was deleted: read the folder again
                        Log("change failed (" + Describe(w) + "): " + error); Changed(); return;
                    }
                    lastError = "";
                    current = shown;
                    if (isNew)
                    {
                        if (rot.ShownNew(shown.Id, new HashSet<string>(Pool().Select(x => x.Id)))) Log("new round");
                        WriteList(RoundFile, rot.Round);
                        WriteList("history.txt", rot.History);
                    }
                    else rot.ShownFromHistory(fromHistory);
                    if (keepAfterChange) { keepAfterChange = false; settings.KeepId = shown.Id; settings.Save(); Log("keeping " + Describe(shown)); }
                    else if (settings.KeepId.Length > 0 && settings.KeepId != shown.Id) { settings.KeepId = ""; settings.Save(); }
                    Log("wallpaper " + Describe(shown) + " " + how + (isNew ? "" : " (from history)") + " - " + Stats());
                    if (lockScreen) UpdateLockScreen(file);
                    Changed();
                    if (queued > 0) { queued--; Next("pressed again"); }
                    else if (queued < 0) { queued++; Previous(); }
                    if (!busy) PrepareNext();
                });
            });
        }

        // Runs a on the UI thread (from a worker); nothing if the app is closing.
        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // The lock screen follows on the side: it can be slow (or hang, just after waking), and must never hold up a
        // change. One update at a time; if one is still running, this picture is skipped (the next change catches up).
        void UpdateLockScreen(string file)
        {
            if (Interlocked.CompareExchange(ref lockScreenBusy, 1, 0) != 0) return;
            var t = new Thread(delegate ()
            {
                try { LockScreenPicture.Set(file); }
                catch (Exception ex) { Log("lock screen failed: " + (ex.InnerException ?? ex).Message); }
                finally { Interlocked.Exchange(ref lockScreenBusy, 0); }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Download the next one ahead of time, so the change itself is instant, then tidy the cache.
        void PrepareNext()
        {
            var next = PickNext();
            if (next == null) return;
            prepared = next;
            var protect = new HashSet<string>(rot.History.Skip(Math.Max(0, rot.History.Count - KeepCached))) { next.Id };
            if (current != null) protect.Add(current.Id);
            if (settings.KeepId.Length > 0) protect.Add(settings.KeepId);
            var inList = new HashSet<string>(walls.Select(w => w.Id));
            var shown = new HashSet<string>(rot.Round);
            bool complete = listComplete;
            long limit = (long)settings.CacheLimitGB << 30;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Fetch(next); } catch (Exception) { /* fetched again when it's due */ }
                try { Housekeep(protect, inList, complete, shown, limit); } catch (Exception ex) { Log("cache: " + ex.Message); }
            });
        }

        // Originals are kept, so a wallpaper is downloaded once and later rounds cost nothing. Removed: wallpapers that
        // have left the list (older than the range, or taken off Wallhaven), but only when that list was read in full;
        // over the size limit, wallpapers already shown this round go first. Never touched: the current one (Windows
        // reads it again, e.g. when Explorer restarts), the next one, and recent history for "Previous".
        void Housekeep(HashSet<string> protect, HashSet<string> inList, bool complete, HashSet<string> shown, long limit)
        {
            lock (gate)
            {
                var keep = new List<FileInfo>();
                long total = 0;
                int gone = 0;
                foreach (var f in new DirectoryInfo(cacheDir).GetFiles())
                {
                    string id = Path.GetFileNameWithoutExtension(f.Name);
                    if (f.Extension == ".part") { if (f.LastWriteTime < DateTime.Now.AddHours(-1)) TryDelete(f); continue; }
                    if (!Wall.IsCacheFile(f.Name)) continue;   // not one of ours: never touched, not counted
                    if (!protect.Contains(id) && complete && inList.Count > 0 && !inList.Contains(id)) { TryDelete(f); gone++; continue; }
                    total += f.Length;
                    if (!protect.Contains(id)) keep.Add(f);
                }
                int trimmed = 0;
                if (total > limit)
                    foreach (var f in keep.OrderBy(f => shown.Contains(Path.GetFileNameWithoutExtension(f.Name)) ? 0 : 1).ThenBy(f => f.LastAccessTime))
                    {
                        if (total <= limit * 9 / 10) break;
                        total -= f.Length; TryDelete(f); trimmed++;
                    }
                if (gone > 0 || trimmed > 0) Log("cache: removed " + gone + " no longer listed, " + trimmed + " over the size limit; " + (total >> 20) + " MB kept");
            }
        }

        static void TryDelete(FileInfo f) { try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // The cache folder was changed (or set for the first time): bring the downloaded originals along.
        void MoveCache(string from, string to)
        {
            Directory.CreateDirectory(to);
            if (string.Equals(Path.GetFullPath(from).TrimEnd('\\'), Path.GetFullPath(to).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from)) return;
            int moved = 0;
            foreach (var f in Directory.GetFiles(from))
                try { string dst = Path.Combine(to, Path.GetFileName(f)); if (!File.Exists(dst)) { File.Move(f, dst); moved++; } else File.Delete(f); }
                catch (IOException) { }
            try { Directory.Delete(from); } catch (IOException) { }
            Log("cache: moved " + moved + " files to " + to);
        }

        public void LockScreenChanged()
        {
            settings.Save();
            if (!settings.LockScreen || current == null || !File.Exists(CachePath(current))) return;
            UpdateLockScreen(CachePath(current));
        }

        // The file to show: the cached original, or the picture itself for one in a folder.
        string CachePath(Wall w) { return w.IsLocal ? w.LocalPath : Path.Combine(cacheDir, w.Id + w.Extension); }

        // The cached original, downloaded first if needed. Asking for one that's already downloading (Next pressed
        // while it's being got ready) waits for that download instead of starting a second one.
        string Fetch(Wall w)
        {
            if (w.IsLocal)
            {
                if (File.Exists(w.LocalPath)) return w.LocalPath;
                throw new FileNotFoundException("the picture isn't there any more (" + w.LocalPath + ")");
            }
            if (w.Image == null && !File.Exists(CachePath(w))) throw new FileNotFoundException("not downloaded, and no longer in the list");
            string path = CachePath(w);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            Task<string> t;
            lock (gate)
                if (!downloads.TryGetValue(w.Id, out t))
                {
                    t = Task.Factory.StartNew(() => Download(w, path), TaskCreationOptions.LongRunning);
                    downloads[w.Id] = t;
                    t.ContinueWith(done => { lock (gate) downloads.Remove(w.Id); });
                }
            try { return t.Result; }
            catch (AggregateException ex) { throw ex.InnerException; }
        }

        // The download runs outside the lock, into its own temporary file, so a slow or dead connection never holds
        // up anything else (another download, the cache tidy-up). A cut-off download is thrown away, not used.
        string Download(Wall w, string path)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
            try
            {
                Directory.CreateDirectory(cacheDir);
                using (var wc = NewClient(30000, 60000))
                {
                    wc.DownloadFile(w.Image, tmp);
                    long expected;
                    if (long.TryParse(wc.ResponseHeaders[HttpResponseHeader.ContentLength], out expected) && new FileInfo(tmp).Length != expected)
                        throw new IOException("the download was cut off");
                }
                if (new FileInfo(tmp).Length == 0) throw new IOException("the download was empty");
                lock (gate)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 0) return path;   // another download got there first
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                return path;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------------- actions

        public void TogglePause()
        {
            settings.Paused = !settings.Paused;
            settings.Save();
            if (!settings.Paused) nextChange = DateTime.Now.AddSeconds(settings.IntervalSeconds);
            Changed();
        }

        // "Keep this wallpaper": no automatic changes until it's taken off again (here, or by Next / Previous / another
        // source). It's remembered across restarts. Pressed during a change, it keeps the one that's coming.
        public void ToggleKeep()
        {
            if (IsKept) { ReleaseKeep("taken off"); return; }
            if (busy) { keepAfterChange = !keepAfterChange; Changed(); return; }
            if (current == null) return;
            settings.KeepId = current.Id;
            settings.Save();
            queued = 0;
            Log("keeping " + Describe(current));
            Changed();
        }

        // Takes "Keep this wallpaper" off; the next automatic change is a full interval away.
        void ReleaseKeep(string why)
        {
            keepAfterChange = false;
            if (settings.KeepId.Length == 0) return;
            settings.KeepId = "";
            settings.Save();
            nextChange = DateTime.Now.AddSeconds(settings.IntervalSeconds);
            Log("stopped keeping (" + why + ")");
            Changed();
        }

        public bool KeepPending { get { return keepAfterChange; } }

        string FavouritePath(Wall w) { return Path.Combine(favouritesDir, w.FileName); }

        // The picture's file, downloaded first if needed (only ever the one on screen, which is downloaded already).
        string FileOf(Wall w) { string p = CachePath(w); return File.Exists(p) ? p : Fetch(w); }

        public void Favourite()
        {
            if (current == null) return;
            try
            {
                Directory.CreateDirectory(favouritesDir);
                string dst = FavouritePath(current);
                if (File.Exists(dst))                                                                   // toggles
                {
                    if (current.IsLocal && SameFolder(current.LocalPath, dst)) ReleaseKeep("taken out of favourites");   // its file is going
                    RemoveFile(dst); Log("unfavourite " + Describe(current));
                }
                else Log((Native.LinkOrCopy(FileOf(current), dst) ? "favourite (linked) " : "favourite (copied) ") + Describe(current));
            }
            catch (Exception ex) { Log("favourite failed: " + ex.Message); }
            folderPool = null;
            Changed();
        }

        // A picture taken out of favourites or a playlist goes to the Recycle Bin (it may be the only copy).
        static void RemoveFile(string path) { if (!Native.Recycle(path)) File.Delete(path); }

        public void Block()
        {
            if (current == null) return;
            ReleaseKeep("hidden");
            blocked.Add(current.Id);
            WriteList("blocked.txt", blocked);
            Log("blocked " + Describe(current));
            rot.Back = -1;   // straight to a new one, even while going back
            if (prepared != null && prepared.Id == current.Id) prepared = null;
            Next("hidden");
        }

        public void ShowInFolder()
        {
            string file = CurrentFile;
            if (file == null || !File.Exists(file)) return;
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------- sources and playlists

        // Something to shuffle: the list, favourites, a playlist (a folder this app makes in PlaylistsFolder) or a
        // folder of your own (used where it is; nothing is copied).
        public class SourceInfo
        {
            public string Key, Name, Folder, Glyph;
            public bool Own;                 // a playlist this app made: it can be renamed and deleted
        }

        public string PlaylistsRoot { get { return settings.PlaylistsFolder; } }
        public bool IsListSource { get { return settings.Source.Length == 0; } }

        // There's something to shuffle: a list address, or the chosen folder exists.
        public bool HasSource
        {
            get
            {
                if (IsListSource) return !string.IsNullOrEmpty(settings.ListUrl);
                string f = SourceFolder(settings.Source);
                return f != null && Directory.Exists(f);
            }
        }

        public string SourceFolder(string key)
        {
            if (key == "favourites") return favouritesDir;
            if (key.StartsWith("playlist:")) return Path.Combine(PlaylistsRoot, key.Substring(9));
            if (key.StartsWith("folder:")) return key.Substring(7);
            return null;
        }

        public string SourceName(string key)
        {
            if (key.Length == 0) return "Wallhaven";
            if (key == "favourites") return "Favourites";
            if (key.StartsWith("playlist:")) return key.Substring(9);
            if (key.StartsWith("folder:")) return FolderLabel(key.Substring(7));
            return key;
        }

        static string FolderLabel(string path)
        {
            string name = Path.GetFileName(path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        bool SourceKnown(string key)
        {
            return key.Length == 0 || key == "favourites" || key.StartsWith("playlist:") || (key.StartsWith("folder:") && folders.Contains(key.Substring(7)));
        }

        public List<SourceInfo> Sources()
        {
            var list = new List<SourceInfo>
            {
                new SourceInfo { Key = "", Name = "Wallhaven", Glyph = Glyphs.Globe },
                new SourceInfo { Key = "favourites", Name = "Favourites", Folder = favouritesDir, Glyph = Glyphs.Heart }
            };
            try
            {
                if (Directory.Exists(PlaylistsRoot))
                    foreach (var d in Directory.GetDirectories(PlaylistsRoot).OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase))
                        if ((new DirectoryInfo(d).Attributes & FileAttributes.Hidden) == 0)
                            list.Add(new SourceInfo { Key = "playlist:" + Path.GetFileName(d), Name = Path.GetFileName(d), Folder = d, Glyph = Glyphs.Library, Own = true });
            }
            catch (Exception ex) { Log("couldn't read the playlists folder: " + ex.Message); }
            foreach (var f in folders) list.Add(new SourceInfo { Key = "folder:" + f, Name = FolderLabel(f), Folder = f, Glyph = Glyphs.Pictures });
            return list;
        }

        public SourceInfo ActiveSource { get { return Sources().FirstOrDefault(x => x.Key == settings.Source); } }

        // Each source keeps its own round (seen.txt for the list, as before).
        string RoundFile
        {
            get
            {
                if (IsListSource) return "seen.txt";
                var safe = new string(settings.Source.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
                if (safe.Length > 60) safe = safe.Substring(0, 40) + "_" + ((uint)settings.Source.GetHashCode()).ToString("x8");
                return "seen-" + safe + ".txt";
            }
        }

        // Shuffle from another source: its round is picked up where it was left, and one of it is shown right away.
        public void SetSource(string key)
        {
            if (key == settings.Source) { if (!busy && current == null) Next("source"); return; }
            if (key == "favourites") try { Directory.CreateDirectory(favouritesDir); } catch (Exception) { }   // none saved yet: "No favourites yet"
            WriteList(RoundFile, rot.Round);
            ReleaseKeep("another source");
            settings.Source = key;
            settings.Save();
            rot.LoadRound(ReadList(RoundFile));
            folderPool = null; prepared = null;
            Log("shuffling from " + SourceName(key));
            Changed();
            if (busy) nextChange = DateTime.Now;   // straight after the change under way
            else if (HasSource && Pool().Count > 0) Next("source");
        }

        public bool InPlaylist(SourceInfo p, Wall w)
        {
            if (p.Folder == null || w == null) return false;
            if (w.IsLocal && SameFolder(Path.GetDirectoryName(w.LocalPath), p.Folder)) return true;
            return File.Exists(Path.Combine(p.Folder, w.FileName));
        }

        static bool SameFolder(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        // Adds the one on screen to a playlist, or takes it out if it's in it already. Added = a link to the same file
        // (no extra space) when the playlist is on the same drive, a copy otherwise.
        public void TogglePlaylist(SourceInfo p)
        {
            if (current == null || p.Folder == null) return;
            try
            {
                Directory.CreateDirectory(p.Folder);
                string dst = Path.Combine(p.Folder, current.FileName);
                bool shownFromIt = current.IsLocal && SameFolder(Path.GetDirectoryName(current.LocalPath), p.Folder);
                if (shownFromIt || File.Exists(dst))
                {
                    if (shownFromIt) ReleaseKeep("taken out of its playlist");   // its file is going
                    RemoveFile(shownFromIt ? current.LocalPath : dst);
                    Log("removed " + Describe(current) + " from " + p.Name);
                }
                else Log((Native.LinkOrCopy(FileOf(current), dst) ? "added (linked) " : "added (copied: another drive) ") + Describe(current) + " to " + p.Name);
            }
            catch (Exception ex) { Log("playlist " + p.Name + ": " + ex.Message); }
            folderPool = null;
            Changed();
        }

        // Why a playlist name can't be used, or null. "except" = the playlist being renamed.
        public string CheckPlaylistName(string name, string except)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Type a name";
            if (name.Length > 60) return "Keep it under 60 characters";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain \\ / : * ? \" < > |";
            if (name.EndsWith(".")) return "A name can't end with a dot";
            string upper = name.ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(upper) || System.Text.RegularExpressions.Regex.IsMatch(upper, @"^(COM|LPT)[1-9]$")) return "Windows reserves that name";
            if (except != null && string.Equals(name, except, StringComparison.Ordinal)) return null;
            if (Sources().Any(x => x.Own && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, except, StringComparison.OrdinalIgnoreCase)))
                return "There's already a playlist called " + name;
            return null;
        }

        public string CreatePlaylist(string name)
        {
            string problem = CheckPlaylistName(name, null);
            if (problem != null) return problem;
            name = name.Trim();
            try { Directory.CreateDirectory(Path.Combine(PlaylistsRoot, name)); }
            catch (Exception ex) { return "Couldn't make it: " + ex.Message; }
            Log("new playlist " + name + " in " + PlaylistsRoot);
            Changed();
            return null;
        }

        public string RenamePlaylist(string oldName, string newName)
        {
            string problem = CheckPlaylistName(newName, oldName);
            if (problem != null) return problem;
            newName = newName.Trim();
            if (newName == oldName) return null;
            try
            {
                string from = Path.Combine(PlaylistsRoot, oldName), to = Path.Combine(PlaylistsRoot, newName);
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    // Only the capitals changed: Windows needs a step in between.
                    string tmp = from + ".renaming";
                    Directory.Move(from, tmp); Directory.Move(tmp, to);
                }
                else Directory.Move(from, to);
                RenameIds(from, to);
            }
            catch (Exception ex) { return "Couldn't rename it: " + ex.Message; }
            string oldKey = "playlist:" + oldName, newKey = "playlist:" + newName;
            if (settings.Source == oldKey)
            {
                // Its round (seen file) moves with it.
                string oldRound = RoundFile;
                settings.Source = newKey;
                try { string a = Path.Combine(Settings.Dir, oldRound), b = Path.Combine(Settings.Dir, RoundFile); if (File.Exists(a)) File.Move(a, b); } catch (Exception) { }
                rot.LoadRound(ReadList(RoundFile));
                settings.Save();
            }
            Log("renamed playlist " + oldName + " to " + newName);
            folderPool = null;
            Changed();
            return null;
        }

        // Pictures of a renamed folder have new paths (their ids): the one on screen, a kept one and history follow.
        void RenameIds(string fromFolder, string toFolder)
        {
            Func<string, string> map = id =>
                id.StartsWith(fromFolder + "\\", StringComparison.OrdinalIgnoreCase) ? toFolder + id.Substring(fromFolder.Length) : id;
            for (int i = 0; i < rot.History.Count; i++) rot.History[i] = map(rot.History[i]);
            WriteList("history.txt", rot.History);
            if (settings.KeepId.Length > 0) settings.KeepId = map(settings.KeepId);
            if (current != null && current.IsLocal)
            {
                string moved = map(current.LocalPath);
                if (moved != current.LocalPath) current = Wall.Local(moved, walls.FirstOrDefault(x => x.Id == Path.GetFileNameWithoutExtension(moved)));
            }
        }

        // A playlist goes to the Recycle Bin (it can be restored from there). Your own folders are only taken off the
        // list, never touched.
        public bool DeleteSource(SourceInfo p)
        {
            if (p == null || p.Key.Length == 0 || p.Key == "favourites") return false;
            if (settings.Source == p.Key) SetSource("");
            if (p.Own)
            {
                bool shown = current != null && current.IsLocal && SameFolder(Path.GetDirectoryName(current.LocalPath), p.Folder);
                if (!Native.Recycle(p.Folder)) { Log("couldn't move playlist " + p.Name + " to the Recycle Bin"); return false; }
                if (shown && settings.KeepId == current.Id) ReleaseKeep("its playlist was deleted");
                Log("deleted playlist " + p.Name + " (in the Recycle Bin)");
            }
            else
            {
                folders.RemoveAll(f => string.Equals(f, p.Folder, StringComparison.OrdinalIgnoreCase));
                WriteList("folders.txt", folders);
                Log("stopped using the folder " + p.Folder);
            }
            Changed();
            return true;
        }

        // A folder of your own pictures, used where it is (nothing is copied), then shuffled from.
        public void AddFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':') path += "\\";   // a whole drive
            if (SameFolder(path, favouritesDir)) { SetSource("favourites"); return; }
            if (SameFolder(Path.GetDirectoryName(path) ?? "", PlaylistsRoot)) { SetSource("playlist:" + Path.GetFileName(path)); return; }
            if (!folders.Any(f => SameFolder(f, path))) { folders.Add(path); WriteList("folders.txt", folders); Log("added the folder " + path); }
            SetSource("folder:" + folders.First(f => SameFolder(f, path)));
        }

        // The dialogs behind "New playlist…", "Rename…", "Delete…" and "Add a folder…", centred on the flyout when it's
        // open. addCurrent: the one on screen goes into the new playlist.
        public void NewPlaylist(bool addCurrent, Form owner)
        {
            string name = FluentDialog.Ask(owner, "New playlist", addCurrent && current != null ? "Name it, and the wallpaper on screen goes in first." : "Name it. Add wallpapers with Add to playlist.",
                "Create", "", n => CheckPlaylistName(n, null));
            if (name == null) return;
            string problem = CreatePlaylist(name);
            if (problem != null) { Log("new playlist: " + problem); return; }
            var p = Sources().FirstOrDefault(x => x.Own && x.Name == name.Trim());
            if (p != null && addCurrent && current != null) TogglePlaylist(p);
        }

        public void RenamePlaylistDialog(SourceInfo p, Form owner)
        {
            if (p == null || !p.Own) return;
            string name = FluentDialog.Ask(owner, "Rename playlist", "A new name for \"" + p.Name + "\".", "Rename", p.Name, n => CheckPlaylistName(n, p.Name));
            if (name != null) { string problem = RenamePlaylist(p.Name, name); if (problem != null) Log("rename: " + problem); }
        }

        public void DeleteSourceDialog(SourceInfo p, Form owner)
        {
            if (p == null || p.Key.Length == 0 || p.Key == "favourites") return;
            int n = 0;
            try { n = Directory.EnumerateFiles(p.Folder).Count(Wall.IsImageFile); } catch (Exception) { }
            bool ok = p.Own
                ? FluentDialog.Confirm(owner, "Delete \"" + p.Name + "\"?", "The playlist and its " + n + " picture" + (n == 1 ? "" : "s") + " go to the Recycle Bin. Wallpapers it shares with the cache or other playlists stay there.", "Delete")
                : FluentDialog.Confirm(owner, "Stop using \"" + p.Name + "\"?", "The folder and its pictures stay where they are; it's only taken off this list.", "Stop using");
            if (ok) DeleteSource(p);
        }

        // A dialog is open (the flyout stays open behind it).
        public static int ModalOpen;

        public void AddFolderDialog(Form owner)
        {
            ModalOpen++;
            Form temp = null;
            try
            {
                if (owner == null || !owner.Visible) owner = temp = FluentDialog.TopOwner();   // from the tray: in front, not behind other windows
                using (var d = new FolderBrowserDialog { Description = "Pick a folder of pictures to shuffle. They're used where they are; nothing is copied.", ShowNewFolderButton = false })
                    if (d.ShowDialog(owner) == DialogResult.OK) AddFolder(d.SelectedPath);
            }
            finally { ModalOpen--; if (temp != null) temp.Dispose(); }
        }

        public void OpenPlaylists() { try { Directory.CreateDirectory(PlaylistsRoot); } catch (Exception) { } Open(PlaylistsRoot); }

        // Undoes every "Never show this one".
        public void UnhideAll()
        {
            Log("unhid " + blocked.Count);
            blocked.Clear();
            WriteList("blocked.txt", blocked);
            Changed();
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
            if (!busy && walls.Count > 0) PrepareNext();   // the favourites minimum may have changed what's next
            Changed();
        }

        // Remember the wallpaper from before, for "Restore my original wallpaper". It's copied next to the app: the
        // file Windows points to may be its own copy under AppData\...\Themes, which each new wallpaper replaces.
        // "none" = a solid colour.
        void RememberOriginal()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                if (k == null) return;
                settings.OriginalWallpaper = Convert.ToString(k.GetValue("WallPaper", ""));
                settings.OriginalStyle = Convert.ToString(k.GetValue("WallpaperStyle", "10"));
                settings.OriginalTile = Convert.ToString(k.GetValue("TileWallpaper", "0"));
            }
            string file = settings.OriginalWallpaper;
            try
            {
                if (file.Length > 0 && File.Exists(file))
                {
                    string ext = Path.GetExtension(file);
                    string copy = Path.Combine(Settings.Dir, "original-wallpaper" + (string.IsNullOrEmpty(ext) ? ".jpg" : ext));
                    File.Copy(file, copy, true);
                    settings.OriginalWallpaper = copy;
                    Log("kept a copy of the wallpaper from before (" + file + ")");
                }
            }
            catch (Exception ex) { Log("couldn't copy the wallpaper from before: " + ex.Message); }
            if (settings.OriginalWallpaper.Length == 0) settings.OriginalWallpaper = "none";
            settings.Save();
        }

        public void RestoreOriginal()
        {
            settings.Paused = true;
            settings.KeepId = ""; keepAfterChange = false;   // the original replaces a kept one
            settings.Save();
            change++; busy = false; queued = 0;  // a change still under way must not cover it up
            string file = settings.OriginalWallpaper;
            string position = settings.OriginalTile == "1" ? "Tile" : settings.OriginalStyle == "6" ? "Fit" : settings.OriginalStyle == "2" ? "Stretch" :
                settings.OriginalStyle == "0" ? "Center" : settings.OriginalStyle == "22" ? "Span" : "Fill";
            if (file == "none")
            {
                ThreadPool.QueueUserWorkItem(delegate { Native.SystemParametersInfo(0x0014, 0, "", 0x01 | 0x02); });   // no picture: the background colour
                Log("restored the solid colour from before (paused)");
            }
            else if (!string.IsNullOrEmpty(file) && File.Exists(file))
            {
                ThreadPool.QueueUserWorkItem(delegate { try { Native.SetWallpaper(file, position); } catch (Exception ex) { Log("restore failed: " + ex.Message); } });
                Log("restored the original wallpaper (paused)");
            }
            else Log("the wallpaper from before (" + file + ") isn't there any more; paused");
            Changed();
        }

        // The program the "Start with Windows" entry starts, or null.
        static string StartupEntry()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                var v = k == null ? null : k.GetValue(AppName) as string;
                return string.IsNullOrEmpty(v) ? null : v.Trim().Trim('"');
            }
        }

        // On only if the entry starts this very copy.
        static bool StartsWithWindows()
        {
            var e = StartupEntry();
            return e != null && string.Equals(Path.GetFullPath(e), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }

        public static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else if (StartsWithWindows()) k.DeleteValue(AppName);   // only this copy's entry
            }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
        }

        // After sleep the network takes a few seconds to come back: change a little later, and refresh the list then.
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action(delegate { OnPowerModeChanged(sender, e); })); return; }
            if (e.Mode == PowerModes.Suspend) Log("going to sleep");
            else if (e.Mode == PowerModes.Resume)
            {
                Log("woke up");
                nextChange = DateTime.Now.AddSeconds(10);
                if (nextListRefresh < DateTime.Now.AddSeconds(30)) nextListRefresh = DateTime.Now.AddSeconds(30);
                Changed();
            }
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
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            for (int i = 1; i <= 5; i++) Native.UnregisterHotKey(Handle, i);
            tray.Visible = false;
            tray.Dispose();
            Log("exit");
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------------- helpers

        // WebClient with time limits: connecting and waiting for the answer (timeout), and each read (readWrite).
        class TimedClient : WebClient
        {
            readonly int timeout, readWrite;
            public TimedClient(int timeout, int readWrite) { this.timeout = timeout; this.readWrite = readWrite; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                r.Timeout = timeout;
                var http = r as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = readWrite;
                return r;
            }
        }

        static WebClient NewClient(int timeout, int readWrite)
        {
            var wc = new TimedClient(timeout, readWrite);
            wc.Headers[HttpRequestHeader.UserAgent] = "WallhavenShuffle-Windows/2.1";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        // The list: the service may still be putting a cold list together (up to ~4 minutes), so this one waits longer.
        static string DownloadList(string url) { using (var wc = NewClient(300000, 300000)) return wc.DownloadString(url); }

        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { /* nothing to open it with */ }
        }

        static IEnumerable<string> ReadList(string name)
        {
            string p = Path.Combine(Settings.Dir, name);
            try { return File.Exists(p) ? File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : new List<string>(); }
            catch (IOException ex) { Log("couldn't read " + name + ": " + ex.Message); return new List<string>(); }
        }

        static void WriteList(string name, IEnumerable<string> items)
        {
            WriteFileSafely(Path.Combine(Settings.Dir, name), string.Join(Environment.NewLine, items) + Environment.NewLine);
        }

        // Writes a new file next to the old one and swaps it in, so a crash or a Windows restart mid-write can't leave
        // a half-written (or empty) file behind.
        public static void WriteFileSafely(string path, string text)
        {
            string tmp = path + ".new";
            try
            {
                File.WriteAllText(tmp, text);
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
                catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }   // e.g. a virus scanner had it open for a moment
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static readonly object logGate = new object();
        public static void Log(string msg)
        {
            lock (logGate)
                try
                {
                    // Appended (a restart mid-write costs one line at most), trimmed to the last 300 lines now and then.
                    string p = Path.Combine(Settings.Dir, "log.txt");
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
                    if (new FileInfo(p).Length > 64 * 1024)
                    {
                        var lines = File.ReadAllLines(p);
                        WriteFileSafely(p, string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 300))) + Environment.NewLine);
                    }
                }
                catch (IOException) { }
        }
    }

    // ==================================================================================================== main window

    // The flyout, kept calm: the wallpaper, one row of controls, and five settings that apply instantly. Less common
    // things live in the "⋯" menu. Acrylic on Windows 11, with rounded corners, border and shadow from Windows itself
    // (a captioned window whose caption is removed, so Windows still treats it as a real window, animations included).
    // It hides when you click elsewhere. Not in the taskbar or Alt+Tab (it is owned by a hidden window). Laid out in 96-DPI pixels and scaled once (fonts are in points).
    class MainWindow : Form
    {
        static readonly int[] Intervals = { 30, 60, 120, 180, 300, 600, 900, 1800, 3600, 7200, 21600, 43200, 86400 };
        const int W = 480, Gutter = 24;

        readonly TrayApp app;
        readonly Settings s;
        readonly Preview preview = new Preview();
        readonly TextLine status = new TextLine();
        readonly RoundButton pinBtn = new RoundButton(Glyphs.Pin, 40, "Keep this wallpaper  (Ctrl+Alt+Shift+K)");
        readonly RoundButton favBtn = new RoundButton(Glyphs.Heart, 40, "Save to favourites  (Ctrl+Alt+Shift+F)");
        readonly RoundButton prevBtn = new RoundButton(Glyphs.Previous, 44, "Previous  (Ctrl+Alt+Shift+←)");
        readonly RoundButton playBtn = new RoundButton(Glyphs.Pause, 56, "Pause  (Ctrl+Alt+Shift+P)") { Primary = true };
        readonly RoundButton nextBtn = new RoundButton(Glyphs.Next, 44, "Next wallpaper  (Ctrl+Alt+Shift+→)");
        readonly RoundButton moreBtn = new RoundButton(Glyphs.More, 40, "More");
        readonly DropDown source = new DropDown { StepWithKeys = false }, every = new DropDown(), fit = new DropDown();
        readonly Toggle pauseGames = new Toggle(), pauseBattery = new Toggle(), startup = new Toggle(), lockScreen = new Toggle();
        readonly ToolTip tips = new ToolTip();
        readonly FluentMenu moreMenu = new FluentMenu();
        readonly Size size;
        ToolStripMenuItem hideItem, openItem, unhideItem, showItem, addToItem;
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
            ControlBox = false; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;

            int y = Gutter, inner = W - 2 * Gutter;
            preview.Bounds = new Rectangle(Gutter, y, inner, inner * 9 / 16); y = preview.Bottom + 14;

            // Keep and favourite (about this wallpaper) | back, play/pause, next | more. The gaps around the middle group
            // are wider, so the three groups read as such.
            var transport = new RoundButton[] { pinBtn, favBtn, prevBtn, playBtn, nextBtn, moreBtn };
            int[] gaps = { 6, 22, 14, 14, 22 };
            int total = transport.Sum(b => b.Width) + gaps.Sum(), x = (W - total) / 2;
            for (int i = 0; i < transport.Length; i++) { var b = transport[i]; b.Location = new Point(x, y + (56 - b.Height) / 2); x += b.Width + (i < gaps.Length ? gaps[i] : 0); }
            y += 56 + 6;
            status.Bounds = new Rectangle(Gutter, y, inner, 22); y = status.Bottom + 18;

            var card = new Card { Bounds = new Rectangle(Gutter, y, inner, 0) };
            card.AddRow("Shuffle from", source);
            card.AddRow("Change wallpaper every", every);
            card.AddRow("Picture fit", fit);
            card.AddRow("Pause during games and full screen", pauseGames);
            card.AddRow("Pause on battery", pauseBattery);
            card.AddRow("Show on the lock screen too", lockScreen);
            card.AddRow("Start with Windows", startup);
            y = card.Bottom + Gutter;

            Controls.AddRange(new Control[] { preview, status, card });
            Controls.AddRange(transport);
            Scale(new SizeF(Theme.Scale, Theme.Scale));
            size = new Size((int)Math.Round(W * Theme.Scale), (int)Math.Round(y * Theme.Scale));
            Size = size;

            // The "⋯" menu: the less common actions. About this wallpaper first, then playlists and the rest.
            addToItem = moreMenu.AddSubmenu("Add to playlist", Glyphs.Add, sub => app.FillAddToMenu(sub, this));
            showItem = moreMenu.Add("Show in folder", Glyphs.FolderOpen, delegate { app.ShowInFolder(); });
            openItem = moreMenu.Add("Open on Wallhaven", Glyphs.Open, delegate { app.OpenOnWallhaven(); });
            hideItem = moreMenu.Add("Never show this one", Glyphs.Hide, delegate { app.Block(); });
            moreMenu.AddSeparator();
            moreMenu.AddSubmenu("Playlists", Glyphs.Library, FillPlaylistsMenu);
            unhideItem = moreMenu.Add("Show hidden wallpapers again", Glyphs.View, delegate { app.UnhideAll(); });
            moreMenu.Add("Restore my original wallpaper", Glyphs.Restore, delegate { app.RestoreOriginal(); });
            moreMenu.Add("Advanced settings", Glyphs.Edit, delegate { app.EditAdvanced(); });
            moreMenu.Opening += delegate
            {
                var w = app.Current;
                hideItem.Enabled = addToItem.Enabled = w != null;
                openItem.Enabled = w != null && w.Page != null;
                showItem.Enabled = app.CurrentFile != null && File.Exists(app.CurrentFile);
                int hidden = app.HiddenCount;
                unhideItem.Enabled = hidden > 0;
                unhideItem.Text = hidden > 0 ? "Show hidden wallpapers again (" + hidden + ")" : "Show hidden wallpapers again";
                moreMenu.RefreshGlyphs();
            };

            foreach (var i in Intervals) every.Items.Add(new KeyValuePair<string, object>(IntervalText(i), i));
            foreach (var p in Settings.Positions) fit.Items.Add(new KeyValuePair<string, object>(p, p));
            source.Actions.Add(new KeyValuePair<string, Action>("New playlist…", delegate { app.NewPlaylist(false, this); }));
            source.Actions.Add(new KeyValuePair<string, Action>("Add a folder of your pictures…", delegate { app.AddFolderDialog(this); }));
            source.BeforeOpen += delegate { FillSources(); };
            LoadValues();

            source.Changed += delegate { if (loading) return; app.SetSource((string)source.Value); };
            every.Changed += delegate { if (loading) return; s.IntervalSeconds = (int)every.Value; app.SettingsChanged(false, false); };
            fit.Changed += delegate { if (loading) return; s.Position = (string)fit.Value; app.SettingsChanged(false, true); };
            pauseGames.CheckedChanged += delegate { if (loading) return; s.PauseFullScreen = pauseGames.Checked; app.SettingsChanged(false, false); };
            pauseBattery.CheckedChanged += delegate { if (loading) return; s.PauseOnBattery = pauseBattery.Checked; app.SettingsChanged(false, false); };
            startup.CheckedChanged += delegate { if (loading) return; TrayApp.SetStartup(startup.Checked); };
            lockScreen.CheckedChanged += delegate { if (loading) return; s.LockScreen = lockScreen.Checked; app.LockScreenChanged(); };

            pinBtn.Click += delegate { app.ToggleKeep(); };
            favBtn.Click += delegate { app.Favourite(); };
            prevBtn.Click += delegate { app.Previous(); };
            playBtn.Click += delegate { app.TogglePause(); };
            nextBtn.Click += delegate { app.Next("window"); };
            moreBtn.Click += delegate { moreMenu.Show(moreBtn, new Point(0, moreBtn.Height + (int)(4 * Theme.Scale))); };
            preview.Click += delegate { var w = app.Current; if (w != null && w.Page != null) app.OpenOnWallhaven(); else app.ShowInFolder(); };
            foreach (var b in transport) tips.SetToolTip(b, b.Tip);
            tips.SetToolTip(status, "Each wallpaper is shown once per round, in random order: this is where you are in it");

            app.StateChanged += OnStateChanged;
            ApplyTheme();
            UpdateView(true, false);   // the picture is loaded when it opens
        }

        static string IntervalText(int sec)
        {
            if (sec < 60) return sec + " seconds";
            if (sec < 3600) return (sec / 60) + (sec == 60 ? " minute" : " minutes");
            return (sec / 3600) + (sec == 3600 ? " hour" : " hours");
        }

        // "Shuffle from": the list, favourites, playlists and your folders (read again each time it opens).
        void FillSources()
        {
            bool was = loading;
            loading = true;
            source.Items.Clear();
            foreach (var src in app.Sources()) source.Items.Add(new KeyValuePair<string, object>(src.Name, src.Key));
            // A playlist renamed or deleted in Explorer while it was being shuffled: say so rather than show another.
            if (!source.Items.Any(i => Equals(i.Value, s.Source))) source.Items.Add(new KeyValuePair<string, object>(app.SourceName(s.Source) + " (not found)", s.Source));
            source.SelectValue(s.Source);
            loading = was;
        }

        // "Playlists ▸": make one, add a folder, rename or delete the one being shuffled, open the folders.
        void FillPlaylistsMenu(FluentMenu sub)
        {
            sub.Add("New playlist…", Glyphs.Add, delegate { app.NewPlaylist(false, this); });
            sub.Add("Add a folder of your pictures…", Glyphs.NewFolder, delegate { app.AddFolderDialog(this); });
            var active = app.ActiveSource;
            if (active != null && active.Own)
            {
                sub.AddSeparator();
                sub.Add("Rename \"" + active.Name + "\"…", Glyphs.Rename, delegate { app.RenamePlaylistDialog(active, this); });
                sub.Add("Delete \"" + active.Name + "\"…", Glyphs.Delete, delegate { app.DeleteSourceDialog(active, this); });
            }
            else if (active != null && active.Key.StartsWith("folder:"))
            {
                sub.AddSeparator();
                sub.Add("Stop using \"" + active.Name + "\"…", Glyphs.Delete, delegate { app.DeleteSourceDialog(active, this); });
            }
            sub.AddSeparator();
            sub.Add("Open playlists folder", Glyphs.Folder, delegate { app.OpenPlaylists(); });
            sub.Add("Open favourites folder", Glyphs.Folder, delegate { app.OpenFavourites(); });
        }

        // Shows the current settings (also after settings.ini was edited).
        public void LoadValues()
        {
            loading = true;
            FillSources();
            if (!every.Items.Any(i => (int)i.Value == s.IntervalSeconds))
                every.Items.Add(new KeyValuePair<string, object>(IntervalText(s.IntervalSeconds), s.IntervalSeconds));
            every.SelectValue(s.IntervalSeconds);
            fit.SelectValue(s.Position);
            pauseGames.Checked = s.PauseFullScreen;
            pauseBattery.Checked = s.PauseOnBattery;
            startup.Checked = app.StartsWithWindowsNow;
            lockScreen.Checked = s.LockScreen;
            loading = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; } // WM_NCCALCSIZE: no caption or borders
            if (m.Msg == 0x0084) { m.Result = new IntPtr(1); return; }                            // WM_NCHITTEST: all client area
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleFlyout(Handle, Theme.Dark, Theme.FlyoutBorder);
            if (Theme.Glass && !Native.Acrylic(Handle)) { Theme.Glass = false; Theme.NoAcrylic = true; ApplyTheme(); }
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // apply the frame change
            Size = size;
        }

        // ---- opening and closing: Windows' own window animation (smooth, done by the compositor; follows the
        // "Animation effects" setting). The flyout is created once and then shown and hidden, so it opens painted.

        public void Open()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(12 * Theme.Scale);
            Location = new Point(area.Right - Width - m, Math.Max(area.Top, area.Bottom - Height - m));
            UpdateView(false, true);
            if (!Visible) Show();
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } // closing = hiding
            base.OnFormClosing(e);
        }

        // Clicking anywhere else closes it (but not while one of its own menus is open).
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed || !Visible || ActiveForm == this || moreMenu.Visible || every.MenuOpen || fit.MenuOpen || source.MenuOpen || TrayApp.ModalOpen > 0) return;
                Hide();
            }));
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Glass ? Color.Black : Theme.Bg; // black client = see-through to the acrylic
            ForeColor = Theme.Text;
            if (IsHandleCreated) Native.StyleFlyout(Handle, Theme.Dark, Theme.FlyoutBorder);
            foreach (Control c in AllControls(this)) c.Invalidate();
            Invalidate();
        }

        static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls) { yield return c; foreach (var d in AllControls(c)) yield return d; }
        }

        void OnStateChanged(object sender, EventArgs e) { if (!IsDisposed) UpdateView(false, false); }

        public void Tick()
        {
            string stats = app.Stats();
            status.Text = app.Status() + (stats.Length > 0 ? "  ·  " + stats : "");
        }

        // picture: also load the preview if the wallpaper changed. Decoding a 4K picture takes a moment, so a hidden
        // window doesn't (it's done when it opens).
        void UpdateView(bool force, bool picture)
        {
            var w = app.Current;
            preview.Overlay = Overlay(w);
            tips.SetToolTip(preview, w != null && w.Page != null ? "Open on Wallhaven" : w != null ? "Show in folder" : "");
            bool kept = app.IsKept || app.KeepPending;
            pinBtn.Glyph = kept ? Glyphs.PinFill : Glyphs.Pin;
            pinBtn.Active = kept;
            pinBtn.Enabled = w != null;
            tips.SetToolTip(pinBtn, kept ? "Stop keeping this wallpaper  (Ctrl+Alt+Shift+K)" : "Keep this wallpaper: no changes until you take it off  (Ctrl+Alt+Shift+K)");
            if (!source.MenuOpen && !Equals(source.Value, s.Source)) FillSources();
            playBtn.Glyph = s.Paused ? Glyphs.Play : Glyphs.Pause;
            prevBtn.Enabled = app.CanGoBack;
            tips.SetToolTip(playBtn, s.Paused ? "Resume  (Ctrl+Alt+Shift+P)" : "Pause  (Ctrl+Alt+Shift+P)");
            favBtn.Glyph = app.IsFavourite ? Glyphs.HeartFill : Glyphs.Heart;
            favBtn.Active = app.IsFavourite;
            tips.SetToolTip(favBtn, app.IsFavourite ? "Remove from favourites  (Ctrl+Alt+Shift+F)" : "Save to favourites  (Ctrl+Alt+Shift+F)");
            favBtn.Enabled = w != null;
            Tick();
            string file = app.CurrentFile;
            if ((picture || Visible) && (force || file != shownFile))
            {
                shownFile = file;
                preview.SetImage(file != null && File.Exists(file) ? file : null);
            }
        }

        // The pill on the picture: resolution and Wallhaven favourites; for a picture of your own, its name.
        static string Overlay(Wall w)
        {
            if (w == null) return null;
            string res = w.Resolution.Length > 0 ? w.Resolution.Replace("x", " × ") : "";
            string tail = w.Favorites >= 0 ? "♥ " + w.Favorites.ToString("N0") : w.WallhavenId == null && w.IsLocal ? Path.GetFileNameWithoutExtension(w.LocalPath) : "";
            if (tail.Length > 40) tail = tail.Substring(0, 39) + "…";
            string text = res.Length > 0 && tail.Length > 0 ? res + "     " + tail : res + tail;
            return text.Length > 0 ? text : null;
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
            switch (keyData)
            {
                case Keys.Escape: Hide(); return true;
                case Keys.Right: app.Next("window"); return true;   // the same keys as the hotkeys, without Ctrl+Alt+Shift
                case Keys.Left: app.Previous(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ==================================================================================================== controls

    // Every control on the flyout draws into a 32-bit bitmap with alpha and copies it to the window as is, so the
    // acrylic shows through its translucent layers (GDI drawing would wipe the alpha).
    abstract class GlassControl : Control
    {
        protected GlassControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                bmp.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Theme.Glass && FindForm() is MainWindow ? Color.Transparent : Parent != null ? Parent.BackColor : Theme.Bg);   // dialogs are solid
                    var surface = Surface; // filled edge to edge, without anti-aliasing (that would leave a faint outline)
                    if (surface.A > 0) using (var b = new SolidBrush(surface)) g.FillRectangle(b, 0, 0, Width, Height);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    Render(g);
                }
                Native.BlitWithAlpha(e.Graphics, bmp);
            }
        }

        // The layer this control sits on: the card's for controls in a settings row, otherwise none.
        protected virtual Color Surface { get { return Parent is Row ? Theme.Card : Color.Transparent; } }
        protected abstract void Render(Graphics g);
        protected static float K { get { return Theme.Scale; } }
    }

    abstract class FluentControl : GlassControl
    {
        protected bool hover, down;
        protected FluentControl()
        {
            SetStyle(ControlStyles.Selectable, true);
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

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (!Focused || !ShowFocusCues) return;
            using (var p = new Pen(Theme.Text, 1.5f * K)) using (var path = Theme.Rounded(RectangleF.Inflate(r, -0.75f * K, -0.75f * K), radius)) g.DrawPath(p, path);
        }
    }

    // One line of secondary text, centred.
    class TextLine : GlassControl
    {
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void Render(Graphics g) { Theme.DrawText(g, Text, Font, ClientRectangle, Theme.TextSecondary, StringAlignment.Center); }
    }

    // The current wallpaper, rounded like a Windows 11 card, with resolution and favourites in a small pill.
    // Keeps only a copy the size of the box (a 4K bitmap is 33 MB).
    class Preview : GlassControl
    {
        Bitmap thumb;
        string overlay;
        public string Overlay { get { return overlay; } set { if (overlay == value) return; overlay = value; Invalidate(); } }

        public Preview() { Cursor = Cursors.Hand; }

        public void SetImage(string file)
        {
            var old = thumb; thumb = null;
            if (old != null) old.Dispose();
            if (file != null && Width > 0 && Height > 0)
            {
                try
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var src = Image.FromStream(fs, false, false))
                    {
                        thumb = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            double scale = Math.Max(Width / (double)src.Width, Height / (double)src.Height); // "Fill", centred
                            float w = (float)(src.Width * scale), h = (float)(src.Height * scale);
                            g.DrawImage(src, (Width - w) / 2, (Height - h) / 2, w, h);
                        }
                    }
                }
                catch (Exception) { thumb = null; }
            }
            Invalidate();
        }

        protected override void Render(Graphics g)
        {
            float k = K;
            using (var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), 8 * k))
            {
                if (thumb != null) using (var tb = new TextureBrush(thumb, WrapMode.Clamp)) g.FillPath(tb, path);
                else
                {
                    using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(70, Theme.Accent), Color.FromArgb(20, Theme.Accent), 120f)) g.FillPath(b, path);
                    Theme.DrawText(g, "Your wallpaper will show up here", Font, ClientRectangle, Theme.TextSecondary, StringAlignment.Center);
                }
                if (overlay != null)
                    using (var f = Theme.Strong(9f))
                    {
                        var ts = Theme.Measure(g, overlay, f);
                        float h = (float)Math.Round(28 * k), w = (float)Math.Round(ts.Width + 24 * k);
                        var pill = new RectangleF((float)Math.Round(12 * k), (float)Math.Round(Height - 12 * k - h), w, h);
                        using (var pp = Theme.Rounded(pill, h / 2)) using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillPath(b, pp);
                        Theme.DrawText(g, overlay, f, pill, Color.White, StringAlignment.Center);
                    }
            }
            using (var edge = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * k - 0.5f))
            using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 24 : 20, Theme.Dark ? Color.White : Color.Black)))
                g.DrawPath(pen, edge);
        }
    }

    // A rounded card of setting rows with thin dividers (Windows 11 Settings style).
    class Card : GlassControl
    {
        const int RowHeight = 56, Pad = 4;
        readonly List<Row> rows = new List<Row>();

        public void AddRow(string title, Control control)
        {
            var row = new Row(title, control, rows.Count > 0) { Bounds = new Rectangle(1, Pad + rows.Count * RowHeight, Width - 2, RowHeight) };
            rows.Add(row);
            Controls.Add(row);
            Height = Pad * 2 + rows.Count * RowHeight;
        }

        protected override void Render(Graphics g)
        {
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * K))
            using (var fill = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // One setting: its name on the left, its control on the right.
    class Row : GlassControl
    {
        readonly bool divider;
        readonly Control control;

        public Row(string title, Control control, bool divider)
        {
            Text = title; this.divider = divider; this.control = control;
            Controls.Add(control);
        }

        protected override Color Surface { get { return Theme.Card; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            bool toggle = control is Toggle;
            int w = (int)Math.Round((toggle ? 92 : 172) * K), h = (int)Math.Round((toggle ? 32 : 34) * K);
            control.Bounds = new Rectangle(Width - w - (int)Math.Round(16 * K), (Height - h) / 2, w, h);
        }

        protected override void Render(Graphics g)
        {
            Theme.DrawText(g, Text, Font, new RectangleF(20 * K, 0, control.Left - 32 * K, Height), Theme.Text, StringAlignment.Near);
            if (divider) using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 0, 0.5f, Width, 0.5f);
        }
    }

    // Windows 11 toggle switch, with its "On" / "Off" label on the left.
    class Toggle : FluentControl
    {
        bool on;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

        protected override void Render(Graphics g)
        {
            float w = (float)Math.Round(40 * K), h = (float)Math.Round(20 * K);
            var r = new RectangleF(Width - w - (float)Math.Round(4 * K), (float)Math.Round((Height - h) / 2), w, h);
            Theme.DrawText(g, on ? "On" : "Off", Font, new RectangleF(0, 0, r.X - 12 * K, Height), Theme.Text, StringAlignment.Far);
            using (var path = Theme.Rounded(r, h / 2))
            {
                if (on) using (var b = new SolidBrush(hover ? Theme.Mix(Theme.Accent, Color.White, 0.1) : Theme.Accent)) g.FillPath(b, path);
                else
                {
                    using (var b = new SolidBrush(hover ? Theme.FieldHover : Theme.ToggleOff)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.TextSecondary, 1f * K)) using (var inner = Theme.Rounded(RectangleF.Inflate(r, -0.5f * K, -0.5f * K), h / 2 - 0.5f * K)) g.DrawPath(p, inner);
                }
            }
            float d = (float)Math.Round((down ? 14 : hover ? 13 : 12) * K);
            float x = on ? r.Right - d - (r.Height - d) / 2 : r.X + (r.Height - d) / 2;
            using (var b = new SolidBrush(on ? Theme.OnAccent : Theme.TextSecondary)) g.FillEllipse(b, x, r.Y + (r.Height - d) / 2, d, d);
            FocusRing(g, RectangleF.Inflate(r, 3 * K, 3 * K), h / 2 + 3 * K);
        }
    }

    // A compact dropdown: the choice and a chevron; opens a menu of the options.
    class DropDown : FluentControl
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public readonly List<KeyValuePair<string, Action>> Actions = new List<KeyValuePair<string, Action>>();   // below the choices ("New playlist…")
        public event EventHandler Changed, BeforeOpen;
        public bool StepWithKeys = true;     // Up / Down change the choice (off where each change does something big)
        int index = -1;
        public bool MenuOpen;
        public object Value { get { return index >= 0 ? Items[index].Value : null; } }

        public void SelectValue(object value)
        {
            index = Items.FindIndex(i => Equals(i.Value, value));
            if (index < 0 && Items.Count > 0) index = 0;
            Invalidate();
        }

        protected override void OnClick(EventArgs e) { Focus(); OpenMenu(); base.OnClick(e); }

        // Up / Down step through the choices without opening the menu, as in Windows.
        protected override bool IsInputKey(Keys key) { return (StepWithKeys && (key == Keys.Up || key == Keys.Down)) || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int to = !StepWithKeys ? -1 : e.KeyCode == Keys.Up ? index - 1 : e.KeyCode == Keys.Down ? index + 1 : -1;
            if (to >= 0 && to < Items.Count && to != index)
            {
                index = to; Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        void OpenMenu()
        {
            if (BeforeOpen != null) BeforeOpen(this, EventArgs.Empty);
            var menu = new FluentMenu { ShowImageMargin = false, ShowCheckMargin = true };
            for (int i = 0; i < Items.Count; i++)
            {
                int n = i;
                var item = new ToolStripMenuItem(Items[i].Key) { Checked = i == index, Padding = FluentMenu.Px(8, 9, 28, 9) };
                item.Click += delegate { if (index == n) return; index = n; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); };
                menu.Items.Add(item);
            }
            if (Actions.Count > 0) menu.AddSeparator();
            foreach (var a in Actions)
            {
                var act = a.Value;
                var item = new ToolStripMenuItem(a.Key) { Padding = FluentMenu.Px(8, 9, 28, 9) };
                item.Click += delegate { BeginInvoke(act); };   // after the menu has closed
                menu.Items.Add(item);
            }
            menu.Closed += delegate { MenuOpen = false; BeginInvoke(new Action(menu.Dispose)); };
            menu.MinimumSize = new Size(Width, 0);
            MenuOpen = true;
            menu.Show(this, new Point(0, Height + (int)(4 * K)));
        }

        protected override void Render(Graphics g)
        {
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 4 * K))
            using (var fill = new SolidBrush(down ? Theme.FieldPressed : hover ? Theme.FieldHover : Theme.Field))
            using (var pen = new Pen(Theme.FieldBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            string text = index >= 0 ? Items[index].Key : "";
            float chevron = (float)Math.Round(36 * K);
            Theme.DrawText(g, text, Font, new RectangleF(12 * K, 0, Width - 12 * K - chevron, Height), Theme.Text, StringAlignment.Near);
            Theme.DrawGlyph(g, Glyphs.Chevron, Theme.Px(g, 8f), new RectangleF(Width - chevron, 0, chevron - 6 * K, Height), Theme.TextSecondary);
            FocusRing(g, r, 4 * K);
        }
    }

    // A round icon button. Primary = filled with the accent colour.
    class RoundButton : FluentControl
    {
        string glyph;
        public bool Primary, Active;
        public readonly string Tip;
        public string Glyph { get { return glyph; } set { if (glyph == value) return; glyph = value; Invalidate(); } }

        public RoundButton(string glyph, int size, string tip) { this.glyph = glyph; Tip = tip; Size = new Size(size, size); }

        protected override void Render(Graphics g)
        {
            var r = new RectangleF(0, 0, Width, Height);
            Color fill, fg;
            if (Primary) { fill = down ? Theme.Mix(Theme.Accent, Color.Black, 0.12) : hover ? Theme.Mix(Theme.Accent, Color.White, 0.12) : Theme.Accent; fg = Theme.OnAccent; }
            else { fill = down ? Theme.Pressed : hover ? Theme.Hover : Color.Transparent; fg = Active ? Theme.Accent : Theme.Text; }
            if (!Enabled) { fill = Color.Transparent; fg = Theme.TextDisabled; }
            if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
            Theme.DrawGlyph(g, glyph, Theme.Px(g, Primary ? 14f : 12f), r, fg);
            if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Text, 1.5f * K)) g.DrawEllipse(p, RectangleF.Inflate(r, -0.75f * K, -0.75f * K));
        }
    }

    // A Windows 11 button with a label: accent-filled (Primary) or the standard subtle one.
    class TextButton : FluentControl
    {
        public bool Primary;
        public TextButton(string text, bool primary) { Text = text; Primary = primary; }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void Render(Graphics g)
        {
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill, border, fg;
            if (!Enabled) { fill = Primary ? Theme.FieldPressed : Theme.Field; border = Theme.FieldBorder; fg = Theme.TextDisabled; }
            else if (Primary) { fill = down ? Theme.Mix(Theme.Accent, Color.Black, 0.12) : hover ? Theme.Mix(Theme.Accent, Color.White, 0.1) : Theme.Accent; border = fill; fg = Theme.OnAccent; }
            else { fill = down ? Theme.FieldPressed : hover ? Theme.FieldHover : Theme.Field; border = Theme.FieldBorder; fg = Theme.Text; }
            using (var path = Theme.Rounded(r, 4 * K)) using (var b = new SolidBrush(fill)) using (var p = new Pen(border)) { g.FillPath(b, path); g.DrawPath(p, path); }
            Theme.DrawText(g, Text, Font, ClientRectangle, fg, StringAlignment.Center);
            FocusRing(g, RectangleF.Inflate(r, 2 * K, 2 * K), 6 * K);
        }
    }

    // A small Windows 11 dialog: a title, a line of explanation, an optional text box (checked as you type, with the
    // reason shown under it), and the primary and Cancel buttons. Enter = the primary button, Esc = Cancel. Solid
    // (not acrylic), with Windows' own rounded corners, border and shadow; in front of the flyout.
    class FluentDialog : Form
    {
        readonly TextBox box;
        readonly Label problem;
        readonly TextButton ok, cancel;
        readonly Func<string, string> check;
        Size content;                        // the dialog's size; applied again once the frame is gone (it would grow by the caption)

        FluentDialog(string title, string message, string okText, string initial, Func<string, string> check, bool asks)
        {
            this.check = check;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body(10f);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            ControlBox = false; MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Func<int, int> k = v => (int)Math.Round(v * Theme.Scale);
            int w = k(400), pad = k(24), y = pad;

            var head = new Label { Text = title, Font = Theme.Strong(14f), ForeColor = Blend(Theme.Text), AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(pad, y, w - 2 * pad, k(32)) };
            y = head.Bottom + k(8);
            var body = new Label { Text = message, ForeColor = Blend(Theme.TextSecondary), AutoSize = false, Bounds = new Rectangle(pad, y, w - 2 * pad, 10) };
            using (var g = CreateGraphics()) body.Height = (int)Math.Ceiling(g.MeasureString(message, Font, body.Width).Height) + k(2);
            y = body.Bottom + k(16);
            Controls.Add(head); Controls.Add(body);

            if (asks)
            {
                // A Windows 11 text box: a rounded field, with the accent line at its foot while it has the focus.
                var field = new FieldFrame { Bounds = new Rectangle(pad, y, w - 2 * pad, k(34)) };
                box = new TextBox { BorderStyle = BorderStyle.None, Text = initial ?? "", Font = Font, BackColor = Blend(Theme.Field), ForeColor = Theme.Text, MaxLength = 80 };
                box.Bounds = new Rectangle(k(11), (field.Height - box.PreferredHeight) / 2 + k(1), field.Width - k(22), box.PreferredHeight);
                field.Controls.Add(box);
                box.GotFocus += delegate { field.Invalidate(); };
                box.LostFocus += delegate { field.Invalidate(); };
                box.TextChanged += delegate { CheckName(false); };
                Controls.Add(field);
                y = field.Bottom + k(6);
                problem = new Label { ForeColor = Theme.Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28), AutoSize = false, Bounds = new Rectangle(pad, y, w - 2 * pad, k(20)) };
                Controls.Add(problem);
                y = problem.Bottom + k(12);
            }

            // The footer band, as in Windows 11 dialogs: the buttons share its width.
            var footer = new Panel { Bounds = new Rectangle(0, y, w, k(80)), BackColor = Theme.Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(234, 234, 234) };
            int bw = (w - 2 * pad - k(8)) / 2;
            ok = new TextButton(okText, true) { Bounds = new Rectangle(pad, k(24), bw, k(32)) };
            cancel = new TextButton("Cancel", false) { Bounds = new Rectangle(pad + bw + k(8), k(24), bw, k(32)) };
            ok.Click += delegate { Accept(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            footer.Controls.Add(ok); footer.Controls.Add(cancel);
            Controls.Add(footer);
            content = new Size(w, footer.Bottom);
            Size = content;   // the window's size, as the flyout does: a client size would get the caption added back
            if (box != null) CheckName(false);
        }

        // A translucent theme colour over the dialog's background, as one solid colour (Windows' text boxes need one).
        static Color Blend(Color c) { double a = c.A / 255.0; return Theme.Mix(Theme.Bg, Color.FromArgb(c.R, c.G, c.B), a); }

        // Live: the reason shown, the button greyed. On Enter / the button: only a good name is taken.
        bool CheckName(bool final)
        {
            string why = check == null ? null : check(box.Text);
            bool empty = box.Text.Trim().Length == 0;
            problem.Text = why != null && (!empty || final) ? why : "";
            ok.Enabled = why == null;
            return why == null;
        }

        void Accept()
        {
            if (box != null && !CheckName(true)) { box.Focus(); return; }
            DialogResult = DialogResult.OK;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; } // no caption or borders (Windows keeps the shadow)
            if (m.Msg == 0x0084) { m.Result = new IntPtr(1); return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleFlyout(Handle, Theme.Dark, Theme.FlyoutBorder);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            Size = content;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate(); Native.SetForegroundWindow(Handle);
            if (box != null) { box.Focus(); box.SelectAll(); } else ok.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { if (ActiveControl == cancel) DialogResult = DialogResult.Cancel; else Accept(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Centred on the flyout when it's open, otherwise on the screen.
        DialogResult ShowOver(Form owner)
        {
            var area = owner != null && owner.Visible ? owner.Bounds : Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + (area.Width - content.Width) / 2, area.Top + (area.Height - content.Height) / 2);
            TrayApp.ModalOpen++;
            try { return owner != null && owner.Visible ? ShowDialog(owner) : ShowDialog(); }
            finally { TrayApp.ModalOpen--; }
        }

        // The text typed (trimmed), or null if cancelled.
        public static string Ask(Form owner, string title, string message, string okText, string initial, Func<string, string> check)
        {
            using (var d = new FluentDialog(title, message, okText, initial, check, true))
                return d.ShowOver(owner) == DialogResult.OK ? d.box.Text.Trim() : null;
        }

        public static bool Confirm(Form owner, string title, string message, string okText)
        {
            using (var d = new FluentDialog(title, message, okText, null, null, false))
                return d.ShowOver(owner) == DialogResult.OK;
        }

        // An invisible window in front of everything, to own a standard dialog opened from the tray (otherwise it can
        // open behind other windows).
        public static Form TopOwner()
        {
            var f = new Form { ShowInTaskbar = false, TopMost = true, FormBorderStyle = FormBorderStyle.None, Opacity = 0, StartPosition = FormStartPosition.Manual, Size = new Size(1, 1) };
            var area = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 3);
            f.Show();
            f.Activate();
            return f;
        }

        // The rounded field around the text box.
        class FieldFrame : Panel
        {
            public FieldFrame() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float k = Theme.Scale;
                bool focus = ContainsFocus;
                var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                using (var path = Theme.Rounded(r, 4 * k))
                using (var b = new SolidBrush(focus ? Blend(Theme.Dark ? Color.FromArgb(255, 30, 30, 30) : Color.White) : Blend(Theme.Field)))
                using (var p = new Pen(Theme.FieldBorder))
                { g.FillPath(b, path); g.DrawPath(p, path); }
                if (focus) using (var a = new SolidBrush(Theme.Accent)) g.FillRectangle(a, 4 * k, Height - 2 * k, Width - 8 * k, 2 * k);
                foreach (Control c in Controls) c.BackColor = focus ? Blend(Theme.Dark ? Color.FromArgb(255, 30, 30, 30) : Color.White) : Blend(Theme.Field);
            }
        }
    }

    // A menu in the Windows 11 style, with icon glyphs (kept in each item's Tag).
    class FluentMenu : ContextMenuStrip
    {
        public FluentMenu()
        {
            Renderer = new FluentMenuRenderer();
            Padding = Px(4, 6, 4, 6);  // ToolStrip spacing isn't scaled for high-DPI screens on its own
            Font = Theme.Body(10f);
            ShowImageMargin = true;
            ShowCheckMargin = false;
        }

        // The click runs once the menu has closed, so a dialog it opens gets the keyboard and the menu is gone first.
        public ToolStripMenuItem Add(string text, string glyph, EventHandler click)
        {
            var i = new ToolStripMenuItem(text, null, (s, e) => { if (IsHandleCreated) BeginInvoke(click, s, e); else click(s, e); }) { Padding = Px(8, 9, 28, 9), Tag = glyph };
            Items.Add(i);
            return i;
        }

        public void AddSeparator() { Items.Add(new ToolStripSeparator { AutoSize = false, Height = (int)Math.Round(13 * Theme.Scale) }); }

        // An item with a submenu, filled afresh each time it opens (playlists come and go).
        public ToolStripMenuItem AddSubmenu(string text, string glyph, Action<FluentMenu> fill)
        {
            var sub = new FluentMenu();
            var item = new ToolStripMenuItem(text) { Padding = Px(8, 9, 28, 9), Tag = glyph };
            item.DropDown = sub;
            sub.Items.Add(new ToolStripMenuItem("…"));   // a submenu shows its arrow (and opens) only with an item in it
            item.DropDownOpening += delegate
            {
                foreach (ToolStripItem old in sub.Items.Cast<ToolStripItem>().ToList()) { if (old.Image != null) old.Image.Dispose(); old.Dispose(); }
                sub.Items.Clear();
                fill(sub);
                sub.RefreshGlyphs();
            };
            Items.Add(item);
            return item;
        }

        public static Padding Px(int left, int top, int right, int bottom)
        {
            Func<int, int> k = v => (int)Math.Round(v * Theme.Scale);
            return new Padding(k(left), k(top), k(right), k(bottom));
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
        protected override void OnOpening(System.ComponentModel.CancelEventArgs e) { base.OnOpening(e); if (ShowImageMargin && Items.Count > 0 && Items[0].Image == null) RefreshGlyphs(); }
    }

    // ==================================================================================================== menus

    // Windows 11 style menus: theme colours, rounded highlight, roomy rows, text centred on the row. The rounded
    // corners and the border come from Windows itself (DWMWA_WINDOW_CORNER_PREFERENCE).
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
            float k = Theme.Scale;
            using (var path = Theme.Rounded(new RectangleF(0, k, e.Item.Width, e.Item.Height - 2 * k), 4 * k))
            using (var b = new SolidBrush(Theme.Hover))
                g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; // the menu is opaque: ClearType is fine
            var color = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
            float x = TextLeft(e.Item);
            Theme.DrawText(e.Graphics, e.Text, e.TextFont, new RectangleF(x, 0, e.Item.Width - x, e.Item.Height), color, StringAlignment.Near);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            // 12 px in from the row's edge, centred on the row like the text
            e.Graphics.DrawImage(e.Image, (int)Math.Round(IconLeft * Theme.Scale), (e.Item.Height - e.Image.Height) / 2, e.Image.Width, e.Image.Height);
        }

        // Row layout (96-DPI pixels): icon 12 px in, 16 px wide, text 12 px after it; a list without icons
        // (the dropdowns) has its selection marker at 8 px and its text at 22 px.
        const int IconLeft = 12;
        static float TextLeft(ToolStripItem item)
        {
            var menu = item.Owner as ToolStripDropDownMenu;
            return (float)Math.Round((menu != null && menu.ShowImageMargin ? IconLeft + 16 + 12 : 22) * Theme.Scale);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Divider)) e.Graphics.DrawLine(p, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            float k = Theme.Scale;
            // The selected choice: a small accent pill, as in Windows 11 lists; with an icon, the pill sits at the row's
            // edge and the icon in its usual place.
            var img = e.Item.Image;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = (float)Math.Round(16 * k);
            using (var b = new SolidBrush(Theme.Accent))
            using (var path = Theme.Rounded(new RectangleF((float)Math.Round((img != null ? 3 : 8) * k), (e.Item.Height - h) / 2, 3 * k, h), 1.5f * k))
                g.FillPath(b, path);
            if (img != null) g.DrawImage(img, (int)Math.Round(IconLeft * k), (e.Item.Height - img.Height) / 2, img.Width, img.Height);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }

        class FluentColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.Menu; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return Theme.Menu; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Menu; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Menu; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Menu; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Menu; } }
            public override Color SeparatorDark { get { return Theme.Menu; } }
            public override Color SeparatorLight { get { return Theme.Menu; } }
            public override Color CheckBackground { get { return Theme.Menu; } }
            public override Color CheckSelectedBackground { get { return Theme.Menu; } }
            public override Color CheckPressedBackground { get { return Theme.Menu; } }
        }
    }
}
