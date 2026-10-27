using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace L2TPConnect
{
    // Палитра и общие примитивы отрисовки в стиле FortiClient.
    internal static class Theme
    {
        public static readonly Color Blue = Color.FromArgb(0x0A, 0x6E, 0xBD);
        public static readonly Color BlueHover = Color.FromArgb(0x18, 0x7B, 0xC8);
        public static readonly Color Navy = Color.FromArgb(0x0B, 0x3A, 0x63);
        public static readonly Color NavyAccent = Color.FromArgb(0x8C, 0xC4, 0xF2);
        public static readonly Color SideLine = Color.FromArgb(0x2A, 0x82, 0xCA);
        public static readonly Color SideMuted = Color.FromArgb(0xC4, 0xDE, 0xF5);

        public static readonly Color Back = Color.FromArgb(0xEE, 0xF2, 0xF5);
        public static readonly Color CardBorder = Color.FromArgb(0xDD, 0xE3, 0xE9);
        public static readonly Color Title = Color.FromArgb(0x4A, 0x60, 0x72);
        public static readonly Color Label = Color.FromArgb(0x5D, 0x72, 0x86);
        public static readonly Color Text = Color.FromArgb(0x1F, 0x29, 0x33);
        public static readonly Color Muted = Color.FromArgb(0x8A, 0x99, 0xA8);
        public static readonly Color FieldBorder = Color.FromArgb(0xC5, 0xCF, 0xD9);
        public static readonly Color FieldReadOnly = Color.FromArgb(0xF3, 0xF5, 0xF7);

        public static readonly Color Primary = Color.FromArgb(0x3D, 0x8B, 0xD8);
        public static readonly Color PrimaryHover = Color.FromArgb(0x2F, 0x7C, 0xC9);
        public static readonly Color PrimaryDown = Color.FromArgb(0x24, 0x6C, 0xB3);
        public static readonly Color Ok = Color.FromArgb(0x3B, 0xAA, 0x4A);
        public static readonly Color Busy = Color.FromArgb(0xE0, 0x8A, 0x1E);
        public static readonly Color Err = Color.FromArgb(0xD0, 0x45, 0x4C);
        public static readonly Color ErrBack = Color.FromArgb(0xFD, 0xEE, 0xEE);
        public static readonly Color OkBack = Color.FromArgb(0xEC, 0xF7, 0xEE);
        public static readonly Color ArtBack = Color.FromArgb(0xE8, 0xF1, 0xFA);
        public static readonly Color Dots = Color.FromArgb(0xC5, 0xCF, 0xD9);

        public static readonly Font Body = new Font("Segoe UI", 10f);
        public static readonly Font BodySemibold = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font Nav = new Font("Segoe UI", 10.5f);
        public static readonly Font NavSelected = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Button = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font PageTitle = new Font("Segoe UI Light", 19f);
        public static readonly Font Status = new Font("Segoe UI Light", 16f);

        // Значки из системного шрифта Segoe MDL2 Assets (Windows 10/11).
        public const string GVpn = "";
        public const string GSettings = "";
        public const string GInfo = "";
        public const string GPerson = "";
        public const string GLaptop = "";
        public const string GGlobe = "";
        public const string GLock = "";
        public const string GUnlock = "";
        public const string GCancel = "";
        public const string GError = "";
        public const string GEye = "";
        public const string GAdd = "";
        public const string GDelete = "";
        public const string GCheck = "";
        public const string GImport = "\uE896";
        public const string GExport = "\uE898";
        public const string GDownload = "\uE896";
        public const string GDesktop = "\uE7F4";
        public const string GEdit = "\uE70F";

        static FontFamily iconFamily;
        static bool iconChecked;

        public static FontFamily IconFamily
        {
            get
            {
                if (!iconChecked)
                {
                    iconChecked = true;
                    try { iconFamily = new FontFamily("Segoe MDL2 Assets"); }
                    catch (ArgumentException) { iconFamily = null; }
                }
                return iconFamily;
            }
        }

        public static void Glyph(Graphics g, string glyph, float px, Color color, RectangleF rect)
        {
            var family = IconFamily;
            if (family == null || px <= 0)
                return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            using (var br = new SolidBrush(color))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                path.AddString(glyph, family, (int)FontStyle.Regular, px, rect, sf);
                g.FillPath(br, path);
            }
            g.SmoothingMode = old;
        }

        public static float Scale(Graphics g)
        {
            return g.DpiX / 96f;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // Значок приложения (синий квадрат с замком) — тот же рисунок, что в .ico.
        public static void AppIcon(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = r.Width;
            using (var path = RoundRect(r, s * 0.22f))
            using (var br = new LinearGradientBrush(r, Color.FromArgb(0x3A, 0x95, 0xFF), Color.FromArgb(0x12, 0x4A, 0xBE), 60f))
                g.FillPath(br, path);
            float bw = s * 0.52f, bh = s * 0.36f;
            float bx = r.X + (s - bw) / 2, by = r.Y + s * 0.46f;
            float sw = s * 0.32f, sx = r.X + (s - sw) / 2, top = r.Y + s * 0.19f;
            using (var pen = new Pen(Color.White, s * 0.08f))
            {
                g.DrawArc(pen, sx, top, sw, sw, 180, 180);
                g.DrawLine(pen, sx, top + sw / 2, sx, by + 1);
                g.DrawLine(pen, sx + sw, top + sw / 2, sx + sw, by + 1);
            }
            using (var body = RoundRect(new RectangleF(bx, by, bw, bh), s * 0.07f))
                g.FillPath(Brushes.White, body);
            using (var hole = new SolidBrush(Color.FromArgb(0x1A, 0x5F, 0xD6)))
            {
                float k = s * 0.09f;
                float kx = r.X + s / 2f - k / 2, ky = by + bh * 0.25f;
                g.FillEllipse(hole, kx, ky, k, k);
                g.FillRectangle(hole, r.X + s / 2f - k * 0.22f, ky + k * 0.6f, k * 0.44f, bh * 0.32f);
            }
        }
    }

    internal static class Fmt
    {
        static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

        public static string Bytes(double b)
        {
            int i = 0;
            while (b >= 1024 && i < Units.Length - 1)
            {
                b /= 1024;
                i++;
            }
            if (i == 0)
                return ((long)b) + " Б";
            return b.ToString(b < 10 ? "0.00" : b < 100 ? "0.0" : "0") + " " + Units[i];
        }

        public static string Speed(double bytesPerSecond)
        {
            return Bytes(bytesPerSecond) + "/с";
        }

        public static string Duration(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            string hms = string.Format("{0:00}:{1:00}:{2:00}", t.Hours, t.Minutes, t.Seconds);
            return t.Days > 0 ? t.Days + "д " + hms : hms;
        }
    }
}
