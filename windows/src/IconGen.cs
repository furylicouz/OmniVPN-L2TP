using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Рисует иконку (синий квадрат с замком) и пишет многоразмерный .ico.
internal static class IconGen
{
    static int Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = Draw(s))
                images.Add(s >= 256 ? Png(bmp) : Dib(bmp));

        using (var w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images)
                w.Write(img);
        }

        if (args.Length > 1)
            using (var big = Draw(256))
                big.Save(args[1], ImageFormat.Png);
        return 0;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad = s <= 16 ? 0 : s * 0.04f;
            var r = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            using (var path = Round(r, s * 0.22f))
            using (var br = new LinearGradientBrush(r, Color.FromArgb(0x3A, 0x95, 0xFF), Color.FromArgb(0x12, 0x4A, 0xBE), 60f))
                g.FillPath(br, path);

            float bw = s * 0.52f, bh = s * 0.36f;
            float bx = (s - bw) / 2, by = s * 0.46f;
            float sw = s * 0.32f, sx = (s - sw) / 2, top = s * 0.19f;
            float stroke = Math.Max(1.6f, s * 0.08f);
            using (var pen = new Pen(Color.White, stroke))
            {
                g.DrawArc(pen, sx, top, sw, sw, 180, 180);
                g.DrawLine(pen, sx, top + sw / 2, sx, by + 1);
                g.DrawLine(pen, sx + sw, top + sw / 2, sx + sw, by + 1);
            }
            using (var body = Round(new RectangleF(bx, by, bw, bh), s * 0.07f))
                g.FillPath(Brushes.White, body);

            if (s >= 24)
            {
                using (var hole = new SolidBrush(Color.FromArgb(0x1A, 0x5F, 0xD6)))
                {
                    float k = s * 0.09f;
                    float kx = s / 2f - k / 2, ky = by + bh * 0.25f;
                    g.FillEllipse(hole, kx, ky, k, k);
                    g.FillRectangle(hole, s / 2f - k * 0.22f, ky + k * 0.6f, k * 0.44f, bh * 0.32f);
                }
            }
        }
        return bmp;
    }

    static GraphicsPath Round(RectangleF r, float radius)
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

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Классический DIB для ICO: BITMAPINFOHEADER, BGRA снизу вверх, пустая AND-маска.
    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        int maskStride = ((s + 31) / 32) * 4;
        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var px = new byte[stride * s];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);

        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40);
            w.Write(s);
            w.Write(s * 2);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(0);
            w.Write(s * s * 4 + maskStride * s);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                w.Write(px, y * stride, s * 4);
            w.Write(new byte[maskStride * s]);
            w.Flush();
            return ms.ToArray();
        }
    }
}
