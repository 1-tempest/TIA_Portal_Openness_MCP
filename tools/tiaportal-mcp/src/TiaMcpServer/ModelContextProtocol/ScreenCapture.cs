using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Desktop / window capture on the server (Win32). Fallback for looking at the HMI simulation
    /// when the browser route is not available. Needs an interactive desktop: a server running as a
    /// service in session 0 gets black images.
    /// </summary>
    internal static class ScreenCapture
    {
        public sealed class Result
        {
            public byte[] Png = Array.Empty<byte>();
            public int Width;
            public int Height;
            public string? WindowTitle;
            public Rectangle Bounds;
            public bool LooksBlank;
        }

        public static Result CaptureDesktop(Rectangle? crop)
        {
            EnsureDpiAware();
            var bounds = new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("No desktop available (is the server running in a non-interactive session?).");

            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }
                return Finish(bmp, crop, null, bounds);
            }
        }

        public static Result CaptureWindow(string titleRegex, Rectangle? crop)
        {
            EnsureDpiAware();
            var regex = new Regex(titleRegex, RegexOptions.IgnoreCase);
            IntPtr found = IntPtr.Zero;
            string? foundTitle = null;
            var titles = new List<string>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var title = GetTitle(h);
                if (string.IsNullOrWhiteSpace(title)) return true;
                titles.Add(title);
                if (found == IntPtr.Zero && regex.IsMatch(title))
                {
                    found = h;
                    foundTitle = title;
                }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero)
            {
                throw new InvalidOperationException($"No visible window title matches '{titleRegex}'. Visible windows: " +
                                                    string.Join(" | ", titles.GetRange(0, Math.Min(30, titles.Count))));
            }

            if (!GetWindowRect(found, out var r)) throw new InvalidOperationException("GetWindowRect failed.");
            var bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException($"Window '{foundTitle}' has no size (minimized?).");

            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                bool printed;
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { printed = PrintWindow(found, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!printed)
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    }
                }
                return Finish(bmp, crop, foundTitle, bounds);
            }
        }

        private static Result Finish(Bitmap bmp, Rectangle? crop, string? title, Rectangle bounds)
        {
            Bitmap output = bmp;
            try
            {
                if (crop is Rectangle c && c.Width > 0 && c.Height > 0)
                {
                    var rect = Rectangle.Intersect(c, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    if (rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("Crop rectangle is outside the captured image.");
                    output = bmp.Clone(rect, bmp.PixelFormat);
                }
                using (var ms = new MemoryStream())
                {
                    output.Save(ms, ImageFormat.Png);
                    return new Result
                    {
                        Png = ms.ToArray(),
                        Width = output.Width,
                        Height = output.Height,
                        WindowTitle = title,
                        Bounds = bounds,
                        LooksBlank = IsUniform(output)
                    };
                }
            }
            finally
            {
                if (!ReferenceEquals(output, bmp)) output.Dispose();
            }
        }

        private static bool IsUniform(Bitmap b)
        {
            var first = b.GetPixel(0, 0);
            for (int i = 1; i <= 16; i++)
            {
                var px = b.GetPixel(b.Width * i / 17, b.Height * ((i * 7) % 17) / 17);
                if (px != first) return false;
            }
            return true;
        }

        private static string GetTitle(IntPtr h)
        {
            var len = GetWindowTextLength(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool _dpiAware;
        private static void EnsureDpiAware()
        {
            if (_dpiAware) return;
            try { SetProcessDPIAware(); } catch { }
            _dpiAware = true;
        }

        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const uint PW_RENDERFULLCONTENT = 2;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    }
}
